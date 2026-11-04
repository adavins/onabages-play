using System.Text.Json;
using System.Text.Json.Nodes;
using OnaBages.Audio;

namespace OnaBages.Engine.Data;

/// <summary>Resultat de llegir la configuració.</summary>
/// <param name="Config">Configuració llegida (buida si no n'hi havia o era il·legible).</param>
/// <param name="Problem">Si el fitxer no s'ha pogut fer servir, el motiu (en català).</param>
public sealed record ConfigLoadResult(EngineConfig Config, string? Problem);

/// <summary>
/// Repositori de la configuració (<c>%APPDATA%\OnaBages\config.json</c>).
/// És l'únic codi que llegeix o escriu aquest fitxer (CLAUDE.md, «Dades»).
/// </summary>
public sealed class ConfigRepository
{
    public const int SchemaVersion = 1;

    private readonly Lock _sync = new();

    /// <param name="path">Ruta del fitxer. Per defecte, la de l'usuari; les proves en passen una de temporal.</param>
    public ConfigRepository(string? path = null)
    {
        FilePath = path ?? Path.Combine(RuntimeFile.DirectoryPath, "config.json");
    }

    public string FilePath { get; }

    /// <summary>On es guarda una còpia del fitxer quan no es pot llegir.</summary>
    public string DamagedCopyPath => FilePath + ".malmes";

    /// <summary>
    /// Llegeix la configuració. Mai no llança excepcions: si el fitxer és malmès,
    /// en desa una còpia a <see cref="DamagedCopyPath"/> i retorna una configuració buida.
    /// </summary>
    public ConfigLoadResult Load()
    {
        lock (_sync)
        {
            if (!File.Exists(FilePath))
                return new ConfigLoadResult(new EngineConfig(), Problem: null);

            try
            {
                var root = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject
                    ?? throw new FormatException("no és un objecte JSON");
                return new ConfigLoadResult(Parse(Migrate(root)), Problem: null);
            }
            catch (Exception ex)
            {
                string problem = "El fitxer de configuració no es pot llegir; s'arrenca sense assignacions.";
                try
                {
                    File.Copy(FilePath, DamagedCopyPath, overwrite: true);
                    problem += $" Se n'ha desat una còpia a {DamagedCopyPath}.";
                }
                catch (IOException) { /* sense còpia; el motiu ja es diu igualment */ }
                catch (UnauthorizedAccessException) { }

                return new ConfigLoadResult(new EngineConfig(), Problem: $"{problem} ({ex.Message})");
            }
        }
    }

    /// <summary>Desa la configuració amb escriptura atòmica.</summary>
    public void Save(EngineConfig config)
    {
        var root = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["players"] = WriteAssignments(config.Players),
            ["recorders"] = WriteAssignments(config.Recorders),
            ["knownDevices"] = new JsonArray(config.KnownDevices.Select(d => (JsonNode)WriteDevice(d)).ToArray()),
            ["ui"] = new JsonObject { ["zoom"] = config.UiZoom },
            ["recorderSettings"] = new JsonArray(config.RecorderSettings
                .OrderBy(s => s.Key, StringComparer.Ordinal)
                .Select(s => (JsonNode)new JsonObject
                {
                    ["id"] = s.Key,
                    ["name"] = s.Value.Name,
                    ["folder"] = s.Value.Folder,
                    ["gainDb"] = s.Value.GainDb,
                }).ToArray()),
        };
        string text = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        lock (_sync)
            AtomicFile.WriteAllText(FilePath, text);
    }

    /// <summary>
    /// Porta un fitxer antic al format actual. Ara només hi ha la versió 1;
    /// quan canviï el format, aquí s'hi afegirà el pas de la versió anterior a la nova.
    /// </summary>
    private static JsonObject Migrate(JsonObject root)
    {
        int? version = root["schemaVersion"] is JsonValue v && v.TryGetValue(out int i) ? i : null;
        return version switch
        {
            SchemaVersion => root,
            null => throw new FormatException("falta schemaVersion"),
            _ => throw new FormatException($"versió {version} desconeguda (aquest motor llegeix la {SchemaVersion})"),
        };
    }

    // -----------------------------------------------------------------------
    // Lectura
    // -----------------------------------------------------------------------

    private static EngineConfig Parse(JsonObject root)
    {
        var config = new EngineConfig();
        ReadAssignments(root["players"], config.Players);
        ReadAssignments(root["recorders"], config.Recorders);

        // "ui" és opcional: els fitxers d'abans no el tenen i es fa servir el valor per defecte.
        if (root["ui"]?["zoom"] is JsonValue zoom && zoom.TryGetValue(out double z))
            config.UiZoom = Math.Clamp(z, EngineConfig.MinZoom, EngineConfig.MaxZoom);

        // "recorderSettings" també és opcional (afegit a la fase 2).
        if (root["recorderSettings"] is JsonArray recorderSettings)
        {
            foreach (var item in recorderSettings)
            {
                string? id = Text(item?["id"]);
                if (string.IsNullOrEmpty(id)) continue;
                double gain = item?["gainDb"] is JsonValue g && g.TryGetValue(out double db) ? db : 0;
                config.RecorderSettings[id] = new RecorderSettings(Text(item?["name"]), Text(item?["folder"]), gain);
            }
        }

        if (root["knownDevices"] is JsonArray devices)
        {
            foreach (var node in devices)
            {
                if (node is JsonObject d && ReadDevice(d) is { } device)
                    config.KnownDevices.Add(device);
            }
        }
        return config;
    }

    private static void ReadAssignments(JsonNode? node, Dictionary<string, DeviceRef> target)
    {
        if (node is not JsonArray list)
            return;

        foreach (var item in list)
        {
            string? id = Text(item?["id"]);
            string? deviceId = Text(item?["device"]?["id"]);
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(deviceId))
                continue;
            target[id] = new DeviceRef(deviceId, Text(item?["device"]?["name"]) ?? "");
        }
    }

    private static AudioDevice? ReadDevice(JsonObject d)
    {
        string? id = Text(d["id"]);
        if (string.IsNullOrEmpty(id))
            return null;

        var kind = Text(d["kind"]) == "input" ? DeviceKind.Input : DeviceKind.Output;
        var status = Text(d["status"]) switch
        {
            "disabled" => DeviceStatus.Disabled,
            "unplugged" => DeviceStatus.Unplugged,
            _ => DeviceStatus.Active,
        };

        DeviceFormat? format = null;
        if (d["format"] is JsonObject f && Int(f["sampleRate"]) is { } rate && Int(f["bits"]) is { } bits && Int(f["channels"]) is { } channels)
            format = new DeviceFormat(rate, bits, channels);

        return new AudioDevice(id, Text(d["name"]) ?? "", kind, status, format);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? s) ? s : null;

    private static int? Int(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out int i) ? i : null;

    // -----------------------------------------------------------------------
    // Escriptura
    // -----------------------------------------------------------------------

    private static JsonArray WriteAssignments(Dictionary<string, DeviceRef> assignments)
    {
        var list = new JsonArray();
        foreach (var (id, device) in assignments.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            list.Add(new JsonObject
            {
                ["id"] = id,
                ["device"] = new JsonObject { ["id"] = device.Id, ["name"] = device.Name },
            });
        }
        return list;
    }

    private static JsonObject WriteDevice(AudioDevice d) => new()
    {
        ["id"] = d.Id,
        ["name"] = d.Name,
        ["kind"] = d.Kind == DeviceKind.Input ? "input" : "output",
        ["status"] = d.Status switch
        {
            DeviceStatus.Disabled => "disabled",
            DeviceStatus.Unplugged => "unplugged",
            _ => "active",
        },
        ["format"] = d.Format is null ? null : new JsonObject
        {
            ["sampleRate"] = d.Format.SampleRate,
            ["bits"] = d.Format.Bits,
            ["channels"] = d.Format.Channels,
        },
    };
}
