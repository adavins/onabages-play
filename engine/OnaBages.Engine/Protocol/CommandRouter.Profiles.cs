using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine.Data;

namespace OnaBages.Engine.Protocol;

/// <summary>
/// Ordres dels perfils (fase 4, pas 1b; D-058): llistar, crear, duplicar, canviar el nom,
/// eliminar i canviar de perfil. Canviar de perfil atura el motor: la interfície el torna a arrencar.
/// </summary>
public sealed partial class CommandRouter
{
    /// <summary>{ profiles: [{ id, name, active, hasPin }] }, ordenats pel nom.</summary>
    private JsonObject ProfileList() => new() { ["profiles"] = ProfilesJson(RequireConfig()) };

    private static JsonArray ProfilesJson(ConfigStore config) => new(config.ListProfiles()
        .Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Profile.Id, ["name"] = p.Profile.Name, ["active"] = p.Active, ["hasPin"] = p.Profile.HasPin,
        })
        .ToArray());

    /// <summary>
    /// profile, pin → { valid }. Un perfil sense PIN sempre és vàlid. El motor no dona mai el PIN:
    /// només diu si és correcte (fase 5, pas 13a).
    /// </summary>
    private JsonObject ProfileCheckPin(JsonObject msg)
    {
        var config = RequireConfig();
        string id = RequireString(msg, "profile");
        string pin = msg["pin"] is JsonValue v && v.TryGetValue(out string? text) ? text
            : throw new BadParamsException("Falta el camp 'pin' (un text).");
        return new JsonObject { ["valid"] = config.CheckPin(id, pin) };
    }

    /// <summary>profile, pin (4 a 8 xifres; null o "" el treu) → { profiles }.</summary>
    private JsonObject ProfileSetPin(JsonObject msg)
    {
        var config = RequireConfig();
        string id = RequireString(msg, "profile");
        if (!msg.ContainsKey("pin"))
            throw new BadParamsException("Falta el camp 'pin' (null per treure'l).");
        string? pin = msg["pin"] switch
        {
            null => null,
            JsonValue v when v.TryGetValue(out string? text) => text.Length == 0 ? null : text,
            _ => throw new BadParamsException("'pin' ha de ser un text o null."),
        };
        config.SetPin(id, pin);
        return new JsonObject { ["profiles"] = ProfilesJson(config) };
    }

    /// <summary>name; opcional copyFrom (id d'un perfil): si hi és, el nou és una còpia d'aquell.</summary>
    private JsonObject ProfileCreate(JsonObject msg)
    {
        var config = RequireConfig();
        string name = ReadString(msg["name"]) ?? throw new BadParamsException("Falta el camp 'name'.");
        string? copyFrom = ReadString(msg["copyFrom"]);
        var created = config.CreateProfile(name, copyFrom);
        return new JsonObject
        {
            ["profile"] = new JsonObject { ["id"] = created.Id, ["name"] = created.Name },
            ["profiles"] = ProfilesJson(config),
        };
    }

    /// <summary>profile (id del perfil), name. La carpeta del perfil no canvia.</summary>
    private JsonObject ProfileRename(JsonObject msg)
    {
        var config = RequireConfig();
        string id = RequireString(msg, "profile");
        string name = ReadString(msg["name"]) ?? throw new BadParamsException("Falta el camp 'name'.");
        config.RenameProfile(id, name);
        ApplyRecordingArchive(config); // si és l'actiu, l'historial va a la carpeta amb el nom nou
        return new JsonObject { ["profiles"] = ProfilesJson(config), ["settings"] = SettingsJson(config) };
    }

    /// <summary>profile (id del perfil). L'actiu no es pot eliminar.</summary>
    private JsonObject ProfileDelete(JsonObject msg)
    {
        var config = RequireConfig();
        config.DeleteProfile(RequireString(msg, "profile"));
        return new JsonObject { ["profiles"] = ProfilesJson(config) };
    }

    /// <summary>
    /// profile (id del perfil). Si no hi ha res actiu, deixa triat el perfil i atura el motor (com engine.shutdown):
    /// el nombre de reproductors i d'enregistradors només es pot canviar en arrencar.
    /// La interfície ha de tornar a arrencar el motor.
    /// </summary>
    private JsonObject ProfileSwitch(JsonObject msg)
    {
        var config = RequireConfig();
        string id = RequireString(msg, "profile");

        var busy = WhatIsBusy();
        if (busy.Count > 0)
            throw new ProfileException("bad_state",
                $"Ara no es pot canviar de perfil: {string.Join(", ", busy)}. Atura-ho primer (el motor s'ha de reiniciar).");

        var profile = config.SwitchProfile(id);

        _ = Task.Run(async () =>
        {
            await Task.Delay(200); // deixa temps perquè la resposta arribi al client
            _requestShutdown();
        });

        return new JsonObject
        {
            ["switching"] = true,
            ["profile"] = new JsonObject { ["id"] = profile.Id, ["name"] = profile.Name },
        };
    }

    /// <summary>profile (id del perfil), path (ruta completa del .zip; si ja hi és, se substitueix).</summary>
    private JsonObject ProfileExport(JsonObject msg)
    {
        var config = RequireConfig();
        string id = RequireString(msg, "profile");
        string path = RequireFullPath(msg, "path");
        var profile = config.ExportProfile(id, path);
        return new JsonObject
        {
            ["profile"] = new JsonObject { ["id"] = profile.Id, ["name"] = profile.Name },
            ["path"] = path,
        };
    }

    /// <summary>path (ruta completa del .zip); opcional name (si no, el del perfil del .zip).</summary>
    private JsonObject ProfileImport(JsonObject msg)
    {
        var config = RequireConfig();
        string path = RequireFullPath(msg, "path");
        string? name = ReadString(msg["name"]);
        var imported = config.ImportProfile(path, name);
        return new JsonObject
        {
            ["profile"] = new JsonObject { ["id"] = imported.Id, ["name"] = imported.Name },
            ["profiles"] = ProfilesJson(config),
        };
    }

    private static string RequireFullPath(JsonObject msg, string field)
    {
        string path = RequireString(msg, field);
        if (!Path.IsPathFullyQualified(path))
            throw new BadParamsException($"'{field}' ha de ser una ruta completa.");
        return path;
    }

    /// <summary>Què impedeix reiniciar el motor ara (en català, per a l'operador). Buit = res.</summary>
    private List<string> WhatIsBusy()
    {
        var busy = new List<string>();
        foreach (var recorder in _state.Recorders.Where(r => r.State == RecorderState.Recording))
            busy.Add($"l'enregistrador {recorder.Id} enregistra");
        foreach (var player in _state.Players.Where(p => p.State == PlayerState.Playing))
            busy.Add($"el reproductor {player.Id} sona");
        if (_state.Cue.State == CueState.Playing)
            busy.Add("la preescolta sona");
        if (_state.Editor.Busy is not null)
            busy.Add("l'editor està obrint o desant un fitxer");
        else if (_state.Editor.Edit?.IsModified == true)
            busy.Add("l'editor té canvis sense desar");
        return busy;
    }
}
