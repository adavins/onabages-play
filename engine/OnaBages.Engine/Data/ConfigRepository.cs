using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OnaBages.Audio;

namespace OnaBages.Engine.Data;

/// <summary>Resultat de llegir la configuració.</summary>
/// <param name="Config">Configuració llegida (buida si no n'hi havia o era il·legible).</param>
/// <param name="Problem">Si algun fitxer no s'ha pogut fer servir, el motiu (en català).</param>
public sealed record ConfigLoadResult(EngineConfig Config, string? Problem);

/// <summary>
/// Repositori de la configuració. És l'únic codi que llegeix o escriu aquests fitxers (CLAUDE.md, «Dades»):
/// <list type="bullet">
/// <item><c>config.json</c> (versió 2): el que és de l'ordinador (dispositius coneguts, zoom, sortida de
/// l'editor) i quin és el perfil actiu;</item>
/// <item><c>perfils\&lt;id&gt;\perfil.json</c>: la configuració de treball d'un programa (dispositius dels
/// reproductors, enregistradors i preescolta, distribució, nom del programa...);</item>
/// <item><c>perfils\&lt;id&gt;\llistes\</c>: l'última llista de cada reproductor (la llegeix i l'escriu
/// <see cref="PlaylistFiles"/>; aquí només se'n diu la carpeta).</item>
/// </list>
/// Un <c>config.json</c> de la versió 1 (abans dels perfils) es converteix sol en el primer perfil (D-057).
/// </summary>
public sealed class ConfigRepository
{
    /// <summary>Versió de <c>config.json</c>. La 2 (fase 4) separa la configuració de l'ordinador de la del perfil.</summary>
    public const int SchemaVersion = 2;

    /// <summary>Versió de <c>perfil.json</c>.</summary>
    public const int ProfileSchemaVersion = 1;

    public const string DefaultProfileId = "Principal";
    public const string DefaultProfileName = "Principal";
    public const string ProfileFileName = "perfil.json";
    public const int MaxProfileIdLength = 40;

    private readonly Lock _sync = new();
    private readonly string _dataFolder;

    /// <param name="path">Ruta de <c>config.json</c>. Per defecte, la de la carpeta de dades; les proves en passen
    /// una de temporal. Els perfils són a la carpeta «perfils» del costat.</param>
    public ConfigRepository(string? path = null)
    {
        FilePath = path ?? Path.Combine(RuntimeFile.DirectoryPath, "config.json");
        _dataFolder = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
        ProfilesFolder = Path.Combine(_dataFolder, "perfils");
    }

    public string FilePath { get; }

    /// <summary>Carpeta amb una subcarpeta per perfil.</summary>
    public string ProfilesFolder { get; }

    /// <summary>On es guarda una còpia del fitxer quan no es pot llegir.</summary>
    public string DamagedCopyPath => FilePath + ".malmes";

    /// <summary>Còpia de <c>config.json</c> tal com era abans de convertir-lo al format amb perfils.</summary>
    public string Version1CopyPath => FilePath + ".v1";

    public string ProfileFolder(string profileId) => Path.Combine(ProfilesFolder, profileId);
    public string ProfileFilePath(string profileId) => Path.Combine(ProfileFolder(profileId), ProfileFileName);

    /// <summary>Carpeta de les últimes llistes dels reproductors d'un perfil.</summary>
    public string LastListsFolder(string profileId) => Path.Combine(ProfileFolder(profileId), "llistes");

    /// <summary>Carpeta de les últimes llistes abans dels perfils (versió 1).</summary>
    private string Version1LastListsFolder => Path.Combine(_dataFolder, "llistes");

    /// <summary>
    /// Llegeix la configuració de l'ordinador i la del perfil actiu. Mai no llança excepcions: si un fitxer
    /// és malmès, en desa una còpia (<c>.malmes</c>) i es continua amb els valors per defecte d'aquella part.
    /// </summary>
    public ConfigLoadResult Load()
    {
        lock (_sync)
        {
            var config = new EngineConfig();
            JsonObject? machine = null;
            string? problem = null;

            if (File.Exists(FilePath))
            {
                try
                {
                    var root = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject
                        ?? throw new FormatException("no és un objecte JSON");
                    int? version = Int(root["schemaVersion"]);
                    if (version == 1)
                        return MigrateFromVersion1(root);
                    if (version is null)
                        throw new FormatException("falta schemaVersion");
                    if (version != SchemaVersion)
                        throw new FormatException($"versió {version} desconeguda (aquest motor llegeix la {SchemaVersion})");
                    machine = root;
                }
                catch (Exception ex)
                {
                    problem = Damaged(FilePath, DamagedCopyPath, "El fitxer de configuració no es pot llegir; s'arrenca sense assignacions.", ex);
                }
            }

            if (machine is not null)
                ReadMachine(machine, config);

            config.ProfileId = ChooseProfile(Text(machine?["activeProfile"]));
            string? profileProblem = ReadProfile(config);
            if (profileProblem is not null)
                problem = problem is null ? profileProblem : $"{problem} {profileProblem}";

            return new ConfigLoadResult(config, problem);
        }
    }

    /// <summary>Desa la configuració (el perfil actiu i la de l'ordinador), amb escriptura atòmica.</summary>
    public void Save(EngineConfig config)
    {
        string profile = ToText(ProfileJson(config));
        string machine = ToText(MachineJson(config));
        lock (_sync)
        {
            // Primer el perfil: config.json només ha d'apuntar a un perfil que ja existeix.
            AtomicFile.WriteAllText(ProfileFilePath(config.ProfileId), profile);
            AtomicFile.WriteAllText(FilePath, machine);
        }
    }

    // -----------------------------------------------------------------------
    // Perfils
    // -----------------------------------------------------------------------

    /// <summary>
    /// El perfil que es farà servir: el que diu config.json si existeix; si no (p. ex. s'ha esborrat
    /// la carpeta a mà), el primer que hi hagi; si no n'hi ha cap, el que deia o el per defecte (es crearà en desar).
    /// </summary>
    private string ChooseProfile(string? wanted)
    {
        bool wantedValid = wanted is not null && IsValidProfileId(wanted);
        if (wantedValid && File.Exists(ProfileFilePath(wanted!)))
            return wanted!;
        try
        {
            if (Directory.Exists(ProfilesFolder))
            {
                string? first = Directory.EnumerateDirectories(ProfilesFolder)
                    .Select(Path.GetFileName)
                    .Where(id => id is not null && IsValidProfileId(id) && File.Exists(ProfileFilePath(id)))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (first is not null)
                    return first;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* es fa servir el per defecte */ }
        return wantedValid ? wanted! : DefaultProfileId;
    }

    /// <summary>Llegeix <c>perfil.json</c> del perfil de <paramref name="config"/>. Retorna el problema, si n'hi ha.</summary>
    private string? ReadProfile(EngineConfig config)
    {
        string path = ProfileFilePath(config.ProfileId);
        config.ProfileName = config.ProfileId; // si el fitxer no diu el nom, el de la carpeta
        if (!File.Exists(path))
            return null;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new FormatException("no és un objecte JSON");
            int? version = Int(root["schemaVersion"]);
            if (version != ProfileSchemaVersion)
                throw new FormatException(version is null ? "falta schemaVersion" : $"versió {version} desconeguda (aquest motor llegeix la {ProfileSchemaVersion})");
            ReadProfileFields(root, config);
            return null;
        }
        catch (Exception ex)
        {
            return Damaged(path, path + ".malmes", $"El perfil «{config.ProfileId}» no es pot llegir; s'arrenca sense les seves assignacions.", ex);
        }
    }

    /// <summary>
    /// Un identificador de perfil és un nom de carpeta: sense camins («..», barres), sense caràcters
    /// que Windows no accepta i sense espais ni punts a les vores.
    /// </summary>
    public static bool IsValidProfileId(string id) =>
        id.Length is > 0 and <= MaxProfileIdLength
        && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && id.Trim().TrimEnd('.') == id
        && !ReservedNames.Contains(id);

    /// <summary>Noms que Windows no deixa fer servir per a una carpeta.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Identificador (nom de carpeta) per a un perfil nou a partir del seu nom: «La Central» → «La Central».
    /// Els caràcters que no poden anar en un nom de carpeta es canvien per «-» i, si ja n'hi ha un
    /// d'igual, s'hi afegeix « (2)», « (3)»...
    /// </summary>
    public string NewProfileId(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string clean = new(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray());
        clean = Regex.Replace(clean, @"\s+", " ").Trim().TrimEnd('.').Trim();
        if (clean.Length > MaxProfileIdLength - 5) // lloc per a « (99)»
            clean = clean[..(MaxProfileIdLength - 5)].TrimEnd().TrimEnd('.').TrimEnd();
        if (clean.Length == 0)
            clean = "Perfil";
        if (ReservedNames.Contains(clean))
            clean += " (perfil)";

        string id = clean;
        for (int n = 2; Directory.Exists(ProfileFolder(id)); n++)
            id = $"{clean} ({n})";
        return id;
    }

    // -----------------------------------------------------------------------
    // Conversió de la versió 1 (abans dels perfils)
    // -----------------------------------------------------------------------

    /// <summary>
    /// La configuració d'abans es converteix en el primer perfil, amb el nom del programa (o «Principal»).
    /// Ordre pensat per a un tall de corrent a mig camí: primer el perfil i les llistes; config.json
    /// (versió 2) al final. Fins llavors, en tornar a arrencar es torna a convertir des del principi.
    /// Les llistes antigues només s'esborren quan tot ja és al seu lloc.
    /// </summary>
    private ConfigLoadResult MigrateFromVersion1(JsonObject root)
    {
        var config = new EngineConfig();
        ReadMachine(root, config);
        ReadProfileFields(root, config);
        config.ProfileName = config.ProgramName.Length > 0 ? config.ProgramName : DefaultProfileName;
        config.ProfileId = NewProfileId(config.ProfileName);

        try
        {
            if (!File.Exists(Version1CopyPath))
                File.Copy(FilePath, Version1CopyPath);

            AtomicFile.WriteAllText(ProfileFilePath(config.ProfileId), ToText(ProfileJson(config)));

            var oldLists = Version1LastLists();
            string listsFolder = LastListsFolder(config.ProfileId);
            Directory.CreateDirectory(listsFolder);
            foreach (string file in oldLists)
                File.Copy(file, Path.Combine(listsFolder, Path.GetFileName(file)), overwrite: true);

            AtomicFile.WriteAllText(FilePath, ToText(MachineJson(config)));

            foreach (string file in oldLists)
            {
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* només ocupa lloc */ }
            }
            return new ConfigLoadResult(config, Problem: null);
        }
        catch (Exception ex)
        {
            // Es pot treballar igualment amb la configuració llegida; es tornarà a provar en arrencar.
            return new ConfigLoadResult(config, $"No s'ha pogut convertir la configuració al format amb perfils ({ex.Message}). Es tornarà a provar en arrencar.");
        }
    }

    /// <summary>Les últimes llistes de la versió 1 (<c>llistes\reproductor-N.obpl</c>). Cap altre fitxer d'aquella carpeta.</summary>
    private List<string> Version1LastLists()
    {
        if (!Directory.Exists(Version1LastListsFolder))
            return [];
        return Directory.EnumerateFiles(Version1LastListsFolder, "reproductor-*" + PlaylistFiles.Extension)
            .Where(f => Regex.IsMatch(Path.GetFileName(f), @"^reproductor-\d+\.obpl$", RegexOptions.IgnoreCase))
            .ToList();
    }

    // -----------------------------------------------------------------------
    // Lectura
    // -----------------------------------------------------------------------

    /// <summary>El que és de l'ordinador: dispositius coneguts, zoom i sortida de l'editor.</summary>
    private static void ReadMachine(JsonObject root, EngineConfig config)
    {
        // "ui" és opcional: els fitxers antics no el tenen i es fa servir el valor per defecte.
        if (root["ui"]?["zoom"] is JsonValue zoom && zoom.TryGetValue(out double z))
            config.UiZoom = Math.Clamp(z, EngineConfig.MinZoom, EngineConfig.MaxZoom);

        // "editor" és opcional (fase 3): sortida de l'editor; sense, la de la preescolta.
        if (Text(root["editor"]?["device"]?["id"]) is { Length: > 0 } editorId)
            config.EditorDevice = new DeviceRef(editorId, Text(root["editor"]?["device"]?["name"]) ?? "");

        if (root["knownDevices"] is JsonArray devices)
        {
            foreach (var node in devices)
            {
                if (node is JsonObject d && ReadDevice(d) is { } device)
                    config.KnownDevices.Add(device);
            }
        }
    }

    /// <summary>El que és del perfil. Serveix per a perfil.json i per al config.json de la versió 1 (són els mateixos camps).</summary>
    private static void ReadProfileFields(JsonObject root, EngineConfig config)
    {
        if (Text(root["name"]) is { } name && CleanProgramName(name) is { Length: > 0 } cleanName)
            config.ProfileName = cleanName;

        ReadAssignments(root["players"], config.Players);
        ReadAssignments(root["recorders"], config.Recorders);
        ReadAssignments(root["cue"], config.Cue);
        if (root["cueSettings"]?["volumeDb"] is JsonValue cueVolume && cueVolume.TryGetValue(out double cueDb))
            config.CueVolumeDb = Math.Clamp(cueDb, -60, 0);

        // Fora de marge, el límit més proper.
        if (Int(root["ui"]?["recordersPerRow"]) is { } perRow)
            config.RecordersPerRow = Math.Clamp(perRow, 1, EngineConfig.MaxRecorders);
        if (Int(root["ui"]?["endWarningSeconds"]) is { } warning)
            config.EndWarningSeconds = Math.Clamp(warning, 0, EngineConfig.MaxEndWarningSeconds);
        if (Int(root["layout"]?["players"]) is { } players)
            config.PlayerCount = Math.Clamp(players, 1, EngineConfig.MaxPlayers);
        if (Int(root["layout"]?["recorders"]) is { } recorders)
            config.RecorderCount = Math.Clamp(recorders, 1, EngineConfig.MaxRecorders);

        // Un nom massa llarg es retalla.
        if (Text(root["program"]?["name"]) is { } programName)
            config.ProgramName = CleanProgramName(programName);

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

    /// <summary>Desa una còpia del fitxer malmès i retorna el missatge per a l'operador.</summary>
    private static string Damaged(string path, string copyPath, string message, Exception ex)
    {
        try
        {
            File.Copy(path, copyPath, overwrite: true);
            message += $" Se n'ha desat una còpia a {copyPath}.";
        }
        catch (IOException) { /* sense còpia; el motiu ja es diu igualment */ }
        catch (UnauthorizedAccessException) { }
        return $"{message} ({ex.Message})";
    }

    /// <summary>
    /// Nom del programa net: sense espais repetits ni salts de línia. Amb <paramref name="cut"/>,
    /// es retalla a <see cref="EngineConfig.MaxProgramName"/> caràcters.
    /// </summary>
    public static string CleanProgramName(string name, bool cut = true)
    {
        string clean = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return cut && clean.Length > EngineConfig.MaxProgramName ? clean[..EngineConfig.MaxProgramName].TrimEnd() : clean;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? s) ? s : null;

    private static int? Int(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out int i) ? i : null;

    // -----------------------------------------------------------------------
    // Escriptura
    // -----------------------------------------------------------------------

    /// <summary>config.json: el que és de l'ordinador i quin perfil és l'actiu.</summary>
    private static JsonObject MachineJson(EngineConfig config) => new()
    {
        ["schemaVersion"] = SchemaVersion,
        ["activeProfile"] = config.ProfileId,
        ["knownDevices"] = new JsonArray(config.KnownDevices.Select(d => (JsonNode)WriteDevice(d)).ToArray()),
        ["ui"] = new JsonObject { ["zoom"] = config.UiZoom },
        ["editor"] = new JsonObject
        {
            ["device"] = config.EditorDevice is { } editorDevice
                ? new JsonObject { ["id"] = editorDevice.Id, ["name"] = editorDevice.Name }
                : null,
        },
    };

    /// <summary>perfil.json: la configuració de treball del programa.</summary>
    private static JsonObject ProfileJson(EngineConfig config) => new()
    {
        ["schemaVersion"] = ProfileSchemaVersion,
        ["name"] = config.ProfileName,
        ["program"] = new JsonObject { ["name"] = config.ProgramName },
        ["layout"] = new JsonObject { ["players"] = config.PlayerCount, ["recorders"] = config.RecorderCount },
        ["ui"] = new JsonObject
        {
            ["recordersPerRow"] = config.RecordersPerRow,
            ["endWarningSeconds"] = config.EndWarningSeconds,
        },
        ["players"] = WriteAssignments(config.Players),
        ["recorders"] = WriteAssignments(config.Recorders),
        ["cue"] = WriteAssignments(config.Cue),
        ["cueSettings"] = new JsonObject { ["volumeDb"] = config.CueVolumeDb },
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

    private static string ToText(JsonObject root) => root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

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
