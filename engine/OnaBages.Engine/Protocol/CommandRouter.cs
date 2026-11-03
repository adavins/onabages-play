using System.Text.Json;
using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine.Data;

namespace OnaBages.Engine.Protocol;

/// <summary>
/// Rep el text d'una ordre, la valida i en retorna la resposta.
/// Totes les ordres noves s'afegeixen aquí (i a docs/protocol.md, i a les proves).
/// </summary>
public sealed class CommandRouter
{
    private readonly EngineState _state;
    private readonly Action _requestShutdown;
    private readonly Func<IReadOnlyList<AudioDevice>> _listDevices;
    private readonly HardwareAssignments? _hardware;
    private readonly ConfigStore? _config;

    /// <param name="state">Estat del motor.</param>
    /// <param name="requestShutdown">Acció que atura el motor (s'executa poc després de respondre).</param>
    /// <param name="listDevices">
    /// Funció que retorna els dispositius d'àudio. Per defecte, els reals de Windows;
    /// les proves hi passen una llista inventada.
    /// </param>
    /// <param name="hardware">Assignacions desades (config.json). Les proves que no en necessiten hi poden passar null.</param>
    /// <param name="config">Configuració (ajustos de la interfície). Igual.</param>
    public CommandRouter(
        EngineState state,
        Action requestShutdown,
        Func<IReadOnlyList<AudioDevice>>? listDevices = null,
        HardwareAssignments? hardware = null,
        ConfigStore? config = null)
    {
        _state = state;
        _requestShutdown = requestShutdown;
        _listDevices = listDevices ?? DeviceList.GetAll;
        _hardware = hardware;
        _config = config;
    }

    public JsonObject Handle(string text)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return Messages.Error(null, "bad_json", "El missatge no és JSON vàlid.");
        }

        if (node is not JsonObject msg)
            return Messages.Error(null, "bad_json", "El missatge ha de ser un objecte JSON.");

        JsonNode? id = msg["id"];

        if (ReadInt(msg["v"]) != Messages.ProtocolVersion)
            return Messages.Error(id, "protocol_version",
                $"Versió de protocol no compatible (s'esperava {Messages.ProtocolVersion}).");

        string? cmd = ReadString(msg["cmd"]);
        if (string.IsNullOrWhiteSpace(cmd))
            return Messages.Error(id, "missing_cmd", "Falta el camp 'cmd'.");

        try
        {
            JsonObject? result = cmd switch
            {
                "engine.hello" => Hello(),
                "engine.ping" => Ping(),
                "state.get" => StateGet(),
                "engine.shutdown" => Shutdown(msg),
                "devices.list" => DevicesList(msg),
                "hardware.report" => HardwareReportGet(),
                "settings.get" => SettingsGet(),
                "settings.set" => SettingsSet(msg),
                "player.setDevice" => PlayerSetDevice(msg),
                "player.load" => PlayerLoad(msg),
                "player.play" => PlayerCommand(msg, p => p.Play()),
                "player.pause" => PlayerCommand(msg, p => p.Pause()),
                "player.stop" => PlayerCommand(msg, p => p.Stop()),
                "player.retry" => PlayerCommand(msg, p => p.Retry()),
                "player.seek" => PlayerSeek(msg),
                "recorder.setDevice" => RecorderSetDevice(msg),
                "recorder.start" => RecorderStart(msg),
                "recorder.stop" => RecorderCommand(msg, r => r.Stop()),
                _ => null,
            };

            return result is null
                ? Messages.Error(id, "unknown_command", $"Ordre desconeguda: {cmd}")
                : Messages.Ok(id, result);
        }
        catch (BadParamsException ex)
        {
            return Messages.Error(id, "bad_params", ex.Message);
        }
        catch (Exception ex) when (ex is PlayerCommandException or RecorderCommandException)
        {
            return Messages.Error(id, "bad_state", ex.Message);
        }
        catch (RecordingActiveException ex)
        {
            return Messages.Error(id, "recording_active", ex.Message);
        }
        catch (RecorderFailedException ex)
        {
            return Messages.Error(id, "recorder_error", ex.Message);
        }
        catch (PlayerFailedException ex)
        {
            return Messages.Error(id, "player_error", ex.Message);
        }
        catch (Exception ex)
        {
            // Un error en una ordre mai no ha d'aturar el motor.
            return Messages.Error(id, "internal", ex.Message);
        }
    }

    private static JsonObject Hello() => new()
    {
        ["name"] = EngineInfo.Name,
        ["version"] = EngineInfo.Version,
        ["protocol"] = Messages.ProtocolVersion,
    };

    private static JsonObject Ping() => new()
    {
        ["pong"] = true,
        ["time"] = DateTimeOffset.UtcNow.ToString("O"),
    };

    private JsonObject StateGet() => new()
    {
        ["engine"] = _state.ToJson(),
        ["players"] = PlayerJson.All(_state.Players),
        ["recorders"] = RecorderJson.All(_state.Recorders),
        ["hardware"] = HardwareJson.Report(_hardware?.Report ?? HardwareReport.Empty),
    };

    /// <summary>Resum de l'arrencada: què ha passat amb els dispositius desats (especificació 5.6).</summary>
    private JsonObject HardwareReportGet() => new()
    {
        ["report"] = HardwareJson.Report(_hardware?.Report ?? HardwareReport.Empty),
    };

    // -----------------------------------------------------------------------
    // Ajustos (es desen a config.json)
    // -----------------------------------------------------------------------

    private ConfigStore RequireConfig() =>
        _config ?? throw new InvalidOperationException("Aquest motor no té configuració.");

    private JsonObject SettingsGet() => new() { ["settings"] = SettingsJson(RequireConfig()) };

    /// <summary>
    /// Canvia només els ajustos que s'envien. Ara: "ui": { "zoom": 0.7..1.5 }.
    /// </summary>
    private JsonObject SettingsSet(JsonObject msg)
    {
        var config = RequireConfig();
        if (msg["ui"] is not JsonObject ui)
            throw new BadParamsException("Falta el camp 'ui' amb els ajustos a canviar.");

        if (ui["zoom"] is JsonNode zoomNode)
        {
            if (zoomNode is not JsonValue v || !v.TryGetValue(out double zoom)
                || zoom < EngineConfig.MinZoom || zoom > EngineConfig.MaxZoom)
                throw new BadParamsException($"'ui.zoom' ha de ser un número entre {EngineConfig.MinZoom} i {EngineConfig.MaxZoom}.");
            config.Update(c => c.UiZoom = Math.Round(zoom, 2));
        }

        return new JsonObject { ["settings"] = SettingsJson(config) };
    }

    private static JsonObject SettingsJson(ConfigStore config) => new()
    {
        ["ui"] = new JsonObject { ["zoom"] = config.Read(c => c.UiZoom) },
    };

    // -----------------------------------------------------------------------
    // Reproductors
    // -----------------------------------------------------------------------

    private Player GetPlayer(JsonObject msg)
    {
        string? id = ReadString(msg["player"]);
        if (string.IsNullOrWhiteSpace(id))
            throw new BadParamsException("Falta el camp 'player' (identificador del reproductor).");
        return _state.FindPlayer(id) ?? throw new BadParamsException($"No existeix el reproductor '{id}'.");
    }

    private static string RequireString(JsonObject msg, string field)
    {
        string? value = ReadString(msg[field]);
        if (string.IsNullOrWhiteSpace(value))
            throw new BadParamsException($"Falta el camp '{field}'.");
        return value;
    }

    private JsonObject PlayerSetDevice(JsonObject msg)
    {
        var player = GetPlayer(msg);
        string deviceId = RequireString(msg, "deviceId");
        player.SetDevice(deviceId);
        // Es desa encara que la sortida falli en obrir-se: és la que ha triat l'operador.
        _hardware?.Remember(ElementKind.Player, player.Id, deviceId, _listDevices());
        return PlayerResult(player);
    }

    private JsonObject PlayerLoad(JsonObject msg)
    {
        var player = GetPlayer(msg);
        player.Load(RequireString(msg, "path"));
        return PlayerResult(player);
    }

    /// <summary>Paràmetre "position": segons des de l'inici de la pista.</summary>
    private JsonObject PlayerSeek(JsonObject msg)
    {
        var player = GetPlayer(msg);
        if (msg["position"] is not JsonValue v || !v.TryGetValue(out double seconds) || double.IsNaN(seconds) || seconds < 0)
            throw new BadParamsException("'position' ha de ser un nombre de segons (0 o més).");

        player.Seek(TimeSpan.FromSeconds(seconds));
        return PlayerResult(player);
    }

    private JsonObject PlayerCommand(JsonObject msg, Action<Player> action)
    {
        var player = GetPlayer(msg);
        action(player);
        return PlayerResult(player);
    }

    /// <summary>
    /// Resposta d'una ordre de reproductor. Si l'ordre l'ha deixat en Error
    /// (dispositiu no disponible, fitxer il·legible), la resposta és un error amb el motiu.
    /// </summary>
    private static JsonObject PlayerResult(Player player)
    {
        if (player.State == PlayerState.Error)
            throw new PlayerFailedException(player.ErrorMessage ?? "Error del reproductor.");
        return new JsonObject { ["player"] = PlayerJson.Player(player) };
    }

    /// <summary>L'ordre ha deixat el reproductor en estat d'error: es respon amb "player_error".</summary>
    private sealed class PlayerFailedException(string message) : Exception(message);

    // -----------------------------------------------------------------------
    // Enregistradors
    // -----------------------------------------------------------------------

    private Recorder GetRecorder(JsonObject msg)
    {
        string? id = ReadString(msg["recorder"]);
        if (string.IsNullOrWhiteSpace(id))
            throw new BadParamsException("Falta el camp 'recorder' (identificador de l'enregistrador).");
        return _state.FindRecorder(id) ?? throw new BadParamsException($"No existeix l'enregistrador '{id}'.");
    }

    private JsonObject RecorderSetDevice(JsonObject msg)
    {
        var recorder = GetRecorder(msg);
        string deviceId = RequireString(msg, "deviceId");
        recorder.SetDevice(deviceId);
        _hardware?.Remember(ElementKind.Recorder, recorder.Id, deviceId, _listDevices());
        // Canviar l'entrada sempre funciona, encara que l'enregistrador tingui un error anterior.
        return new JsonObject { ["recorder"] = RecorderJson.Recorder(recorder) };
    }

    /// <summary>Paràmetres opcionals: "folder" (carpeta de treball) i "name" (per al nom del fitxer).</summary>
    private JsonObject RecorderStart(JsonObject msg)
    {
        var recorder = GetRecorder(msg);
        string? folder = ReadString(msg["folder"]);
        string? name = ReadString(msg["name"]);
        if (folder is not null && !Path.IsPathFullyQualified(folder))
            throw new BadParamsException("'folder' ha de ser una ruta completa (p. ex. D:\\Enregistraments).");

        recorder.Start(string.IsNullOrWhiteSpace(folder) ? null : folder, string.IsNullOrWhiteSpace(name) ? null : name);
        return RecorderResult(recorder);
    }

    private JsonObject RecorderCommand(JsonObject msg, Action<Recorder> action)
    {
        var recorder = GetRecorder(msg);
        action(recorder);
        return RecorderResult(recorder);
    }

    /// <summary>Si l'ordre ha deixat l'enregistrador en Error, la resposta és un error amb el motiu.</summary>
    private static JsonObject RecorderResult(Recorder recorder)
    {
        if (recorder.State == RecorderState.Error)
            throw new RecorderFailedException(recorder.ErrorMessage ?? "Error de l'enregistrador.");
        return new JsonObject { ["recorder"] = RecorderJson.Recorder(recorder) };
    }

    private sealed class RecorderFailedException(string message) : Exception(message);

    private sealed class RecordingActiveException(string message) : Exception(message);

    /// <summary>
    /// Atura el motor. Si hi ha enregistraments en curs es rebutja, tret que es demani
    /// amb "force": true (llavors els fitxers es tanquen correctament abans d'aturar-se).
    /// </summary>
    private JsonObject Shutdown(JsonObject msg)
    {
        bool force = msg["force"] is JsonValue v && v.TryGetValue(out bool b) && b;
        if (_state.IsRecording && !force)
            throw new RecordingActiveException(
                "Hi ha enregistraments en curs. Atura'ls primer (o envia \"force\": true per tancar-los i aturar el motor).");

        _ = Task.Run(async () =>
        {
            await Task.Delay(200); // deixa temps perquè la resposta arribi al client
            _requestShutdown();
        });

        return new JsonObject { ["stopping"] = true };
    }

    /// <summary>
    /// Llista de dispositius d'àudio.
    /// Paràmetres opcionals: "kind" ("output", "input" o "all") i "includeInactive" (bool).
    /// </summary>
    private JsonObject DevicesList(JsonObject msg)
    {
        string kind = ReadString(msg["kind"]) ?? "all";
        if (kind is not ("all" or "output" or "input"))
            throw new BadParamsException("'kind' ha de ser \"output\", \"input\" o \"all\".");

        bool includeInactive = msg["includeInactive"] is JsonValue v && v.TryGetValue(out bool b) && b;

        var list = new JsonArray();
        foreach (var device in _listDevices())
        {
            if (kind == "output" && device.Kind != DeviceKind.Output) continue;
            if (kind == "input" && device.Kind != DeviceKind.Input) continue;
            if (!includeInactive && device.Status != DeviceStatus.Active) continue;

            list.Add(DeviceJson.Device(device));
        }

        return new JsonObject { ["devices"] = list };
    }

    /// <summary>Error en els paràmetres d'una ordre: es respon amb el codi "bad_params".</summary>
    private sealed class BadParamsException(string message) : Exception(message);

    private static int? ReadInt(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out int i) ? i : null;

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? s) ? s : null;
}
