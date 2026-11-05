using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OnaBages.Audio;

namespace OnaBages.Engine.Data;

/// <summary>Resultat de llegir la configuració.</summary>
/// <param name="Config">Configuració llegida (buida si no n'hi havia o era il·legible).</param>
/// <param name="Problem">Si algun fitxer no s'ha pogut fer servir, el motiu (en català).</param>
public sealed record ConfigLoadResult(EngineConfig Config, string? Problem);

/// <summary>Un perfil: <paramref name="Id"/> és el nom de la seva carpeta; <paramref name="Name"/>, el que es mostra.</summary>
public sealed record ProfileInfo(string Id, string Name);

/// <summary>Una operació amb perfils no es pot fer. <paramref name="Code"/> és el codi d'error del protocol.</summary>
public sealed class ProfileException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

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
        && !id.StartsWith('.') // les carpetes temporals (p. ex. d'una importació) comencen per «.»
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
        clean = Regex.Replace(clean, @"\s+", " ").Trim().Trim('.').Trim();
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
    // Operacions amb perfils (pas 1b). Les crida ConfigStore, que sap quin és l'actiu.
    // -----------------------------------------------------------------------

    /// <summary>Perfils que hi ha a la carpeta, ordenats pel nom.</summary>
    public List<ProfileInfo> ListProfiles()
    {
        lock (_sync)
        {
            var list = new List<ProfileInfo>();
            if (!Directory.Exists(ProfilesFolder))
                return list;
            foreach (string folder in Directory.EnumerateDirectories(ProfilesFolder))
            {
                string id = Path.GetFileName(folder);
                if (!IsValidProfileId(id) || !File.Exists(ProfileFilePath(id)))
                    continue;
                list.Add(new ProfileInfo(id, ReadProfileName(id)));
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return list;
        }
    }

    /// <summary>Nom d'un perfil (el de la carpeta si el fitxer no es pot llegir).</summary>
    private string ReadProfileName(string id)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(ProfileFilePath(id)))?["name"] is JsonValue v
                && v.TryGetValue(out string? name) && CleanProgramName(name) is { Length: > 0 } clean)
                return clean;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return id;
    }

    /// <summary>
    /// Crea un perfil nou. Amb <paramref name="copyFromId"/>, és una còpia d'aquell (amb les seves últimes
    /// llistes); si no, un perfil buit (2 reproductors, 2 enregistradors, sense dispositius) amb el programa
    /// del mateix nom. El nom ja ha d'estar comprovat.
    /// </summary>
    public ProfileInfo CreateProfile(string name, string? copyFromId)
    {
        lock (_sync)
        {
            string id = NewProfileId(name);
            string folder = ProfileFolder(id);
            try
            {
                if (copyFromId is not null)
                {
                    CopyFolder(ProfileFolder(copyFromId), folder);
                    var root = JsonNode.Parse(File.ReadAllText(ProfileFilePath(id))) as JsonObject
                        ?? throw new FormatException("no és un objecte JSON");
                    root["name"] = name;
                    AtomicFile.WriteAllText(ProfileFilePath(id), ToText(root));
                }
                else
                {
                    var config = new EngineConfig { ProfileId = id, ProfileName = name, ProgramName = name };
                    AtomicFile.WriteAllText(ProfileFilePath(id), ToText(ProfileJson(config)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
            {
                // A mitges no serveix: es treu el que s'hagi copiat.
                try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
                catch (Exception) { /* es veurà com una carpeta sense perfil i no es llistarà */ }
                throw new ProfileException("file_error", $"No s'ha pogut crear el perfil: {ex.Message}");
            }
            return new ProfileInfo(id, name);
        }
    }

    /// <summary>Canvia el nom d'un perfil que <b>no</b> és l'actiu (el de l'actiu el desa ConfigStore). La carpeta no canvia.</summary>
    public void RenameProfile(string id, string name)
    {
        lock (_sync)
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(ProfileFilePath(id))) as JsonObject
                    ?? throw new FormatException("no és un objecte JSON");
                root["name"] = name;
                AtomicFile.WriteAllText(ProfileFilePath(id), ToText(root));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
            {
                throw new ProfileException("file_error", $"No s'ha pogut canviar el nom del perfil: {ex.Message}");
            }
        }
    }

    /// <summary>Esborra la carpeta d'un perfil que no és l'actiu. Els fitxers d'àudio no hi són mai: només es perden les referències.</summary>
    public void DeleteProfile(string id)
    {
        lock (_sync)
        {
            try
            {
                Directory.Delete(ProfileFolder(id), recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ProfileException("file_error", $"No s'ha pogut eliminar el perfil: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Fa que el perfil <paramref name="id"/> sigui l'actiu a partir del pròxim arrencada del motor:
    /// només canvia <c>activeProfile</c> de config.json (la resta, la de <paramref name="current"/>).
    /// </summary>
    public void SetActiveProfile(EngineConfig current, string id)
    {
        var machine = MachineJson(current);
        machine["activeProfile"] = id;
        string text = ToText(machine);
        lock (_sync)
            AtomicFile.WriteAllText(FilePath, text);
    }

    // -----------------------------------------------------------------------
    // Exportar i importar (pas 1c): un .zip amb perfil.json i llistes\*.obpl
    // -----------------------------------------------------------------------

    /// <summary>Límits d'un .zip importat (un perfil real ocupa uns pocs kB).</summary>
    public const int MaxImportEntries = 200;
    public const long MaxImportEntryBytes = 10 * 1024 * 1024;
    public const long MaxImportTotalBytes = 50 * 1024 * 1024;

    /// <summary>Desa el perfil <paramref name="id"/> en un .zip (substitueix el fitxer si ja hi és).</summary>
    public void ExportProfile(string id, string zipPath)
    {
        lock (_sync)
        {
            string temp = zipPath + ".tmp";
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
                using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
                {
                    zip.CreateEntryFromFile(ProfileFilePath(id), ProfileFileName);
                    string lists = LastListsFolder(id);
                    if (Directory.Exists(lists))
                    {
                        foreach (string file in Directory.EnumerateFiles(lists, "*" + PlaylistFiles.Extension))
                            zip.CreateEntryFromFile(file, "llistes/" + Path.GetFileName(file));
                    }
                }
                File.Move(temp, zipPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                try { File.Delete(temp); } catch (Exception) { /* només queda un temporal */ }
                throw new ProfileException("file_error", $"No s'ha pogut exportar el perfil: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Llegeix el nom del perfil que hi ha dins d'un .zip, i comprova que el .zip és un perfil vàlid.
    /// </summary>
    /// <exception cref="ProfileException">file_error si no es pot llegir o no és un perfil.</exception>
    public string ReadImportName(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var root = ReadImportedProfile(zip);
            return Text(root["name"]) is { } name && CleanProgramName(name) is { Length: > 0 } clean
                ? clean
                : Path.GetFileNameWithoutExtension(zipPath);
        }
        catch (ProfileException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            throw new ProfileException("file_error", $"No s'ha pogut llegir {Path.GetFileName(zipPath)}: {ex.Message}");
        }
    }

    /// <summary>
    /// Importa un perfil d'un .zip com a perfil nou, amb el nom <paramref name="name"/> (ja comprovat).
    /// Del .zip només s'agafen <c>perfil.json</c> i <c>llistes\*.obpl</c>; la resta s'ignora.
    /// </summary>
    public ProfileInfo ImportProfile(string zipPath, string name)
    {
        lock (_sync)
        {
            string id = NewProfileId(name);
            // Primer en una carpeta temporal (no es llista: comença per «.»); quan tot hi és, es reanomena.
            string temp = Path.Combine(ProfilesFolder, ".importacio-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    var root = ReadImportedProfile(zip);
                    root["name"] = name;
                    Directory.CreateDirectory(Path.Combine(temp, "llistes"));
                    AtomicFile.WriteAllText(Path.Combine(temp, ProfileFileName), ToText(root));

                    foreach (var entry in zip.Entries)
                    {
                        if (ImportedListName(entry.FullName) is not { } listName)
                            continue;
                        entry.ExtractToFile(Path.Combine(temp, "llistes", listName), overwrite: true);
                    }
                }
                Directory.Move(temp, ProfileFolder(id));
                return new ProfileInfo(id, name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ProfileException)
            {
                try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
                catch (Exception) { /* només queda una carpeta temporal, que no es llista */ }
                if (ex is ProfileException) throw;
                throw new ProfileException("file_error", $"No s'ha pogut importar el perfil: {ex.Message}");
            }
        }
    }

    /// <summary>Comprova els límits del .zip i en retorna perfil.json (ha de ser un perfil d'aquesta versió).</summary>
    private static JsonObject ReadImportedProfile(ZipArchive zip)
    {
        const string notProfile = "Aquest fitxer no és un perfil d'Ona Bages Play.";
        if (zip.Entries.Count > MaxImportEntries)
            throw new ProfileException("file_error", notProfile);
        long total = 0;
        ZipArchiveEntry? profile = null;
        foreach (var entry in zip.Entries)
        {
            if (entry.Length > MaxImportEntryBytes)
                throw new ProfileException("file_error", $"{notProfile} (conté un fitxer massa gran: {entry.FullName})");
            total += entry.Length;
            if (string.Equals(entry.FullName, ProfileFileName, StringComparison.OrdinalIgnoreCase))
                profile = entry;
        }
        if (total > MaxImportTotalBytes || profile is null)
            throw new ProfileException("file_error", notProfile);

        JsonObject root;
        try
        {
            using var reader = new StreamReader(profile.Open());
            root = JsonNode.Parse(reader.ReadToEnd()) as JsonObject ?? throw new FormatException();
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new ProfileException("file_error", $"{notProfile} (perfil.json és malmès)");
        }
        int? version = Int(root["schemaVersion"]);
        if (version != ProfileSchemaVersion)
            throw new ProfileException("file_error",
                $"El perfil és d'una versió que aquest programa no coneix ({version?.ToString() ?? "sense versió"}). Potser cal actualitzar Ona Bages Play.");
        return root;
    }

    /// <summary>
    /// Nom de fitxer d'una llista del .zip (<c>llistes/xxx.obpl</c>), o null si l'entrada no ho és.
    /// Només un nom de fitxer, sense camins: cap entrada no pot escriure fora de la carpeta del perfil.
    /// </summary>
    private static string? ImportedListName(string entryName)
    {
        string normalized = entryName.Replace('\\', '/');
        if (!normalized.StartsWith("llistes/", StringComparison.OrdinalIgnoreCase))
            return null;
        string file = normalized["llistes/".Length..];
        if (file.Length == 0 || file.Contains('/') || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || file.StartsWith('.') || !file.EndsWith(PlaylistFiles.Extension, StringComparison.OrdinalIgnoreCase))
            return null;
        return file;
    }

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            // Els temporals i les còpies de fitxers malmesos no es copien.
            if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".malmes", StringComparison.OrdinalIgnoreCase))
                continue;
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        foreach (string folder in Directory.EnumerateDirectories(source))
            CopyFolder(folder, Path.Combine(target, Path.GetFileName(folder)));
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

        // Compressor/limitador de cada reproductor (pas 4b; camp opcional, sense canvi de versió).
        if (root["playerProcessing"] is JsonArray processing)
        {
            foreach (var item in processing)
            {
                string? id = Text(item?["id"]);
                if (string.IsNullOrEmpty(id) || item is not JsonObject obj) continue;
                config.PlayerDynamics[id] = ReadDynamics(obj);
            }
        }

        // Presets propis del compressor/limitador (D-069; camp opcional, sense canvi de versió).
        if (root["processingPresets"] is JsonArray presets)
        {
            foreach (var item in presets)
            {
                if (item is not JsonObject obj || Text(obj["id"]) is not { Length: > 0 } presetId
                    || Text(obj["name"]) is not { } presetName || CleanProgramName(presetName) is not { Length: > 0 } cleanPresetName
                    || obj["processing"] is not JsonObject settings)
                    continue;
                config.ProcessingPresets.Add(new ProcessingPreset(presetId, cleanPresetName, ReadDynamics(settings)));
                if (config.ProcessingPresets.Count == EngineConfig.MaxProcessingPresets) break;
            }
        }

        config.AudioFolders = ReadExplorerFolders(root["explorer"]?["audios"]);
        config.ListFolders = ReadExplorerFolders(root["explorer"]?["lists"]);
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
        ["playerProcessing"] = new JsonArray(config.PlayerDynamics
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => (JsonNode)WriteDynamics(p.Key, p.Value)).ToArray()),
        ["processingPresets"] = new JsonArray(config.ProcessingPresets
            .Select(p => (JsonNode)new JsonObject
            {
                ["id"] = p.Id,
                ["name"] = p.Name,
                ["processing"] = WriteDynamics(null, p.Settings),
            }).ToArray()),
        // Explorador del panell lateral (pas 2a; camp opcional, sense canvi de versió).
        ["explorer"] = new JsonObject
        {
            ["audios"] = WriteExplorerFolders(config.AudioFolders),
            ["lists"] = WriteExplorerFolders(config.ListFolders),
        },
    };

    /// <summary>Ajustos del compressor/limitador en JSON (els mateixos noms que al protocol).</summary>
    public static JsonObject WriteDynamics(string? id, DynamicsSettings s)
    {
        var obj = new JsonObject();
        if (id is not null) obj["id"] = id;
        obj["enabled"] = s.Enabled;
        obj["inputGainDb"] = s.InputGainDb;
        obj["thresholdDb"] = s.ThresholdDb;
        obj["ratio"] = s.Ratio;
        obj["attackMs"] = s.AttackMs;
        obj["releaseMs"] = s.ReleaseMs;
        obj["outputGainDb"] = s.OutputGainDb;
        obj["stereoLink"] = s.StereoLink;
        obj["ceilingDb"] = s.CeilingDb;
        return obj;
    }

    /// <summary>
    /// Llegeix ajustos del compressor/limitador. Els camps que no hi són es prenen de <paramref name="current"/>
    /// (per defecte, els valors per defecte); els que són fora de marge es porten al límit.
    /// </summary>
    public static DynamicsSettings ReadDynamics(JsonObject obj, DynamicsSettings? current = null)
    {
        var s = current ?? new DynamicsSettings();
        static double? Number(JsonNode? n) => n is JsonValue v && v.TryGetValue(out double d) ? d : null;
        static bool? Flag(JsonNode? n) => n is JsonValue v && v.TryGetValue(out bool b) ? b : null;
        return (s with
        {
            Enabled = Flag(obj["enabled"]) ?? s.Enabled,
            InputGainDb = Number(obj["inputGainDb"]) ?? s.InputGainDb,
            ThresholdDb = Number(obj["thresholdDb"]) ?? s.ThresholdDb,
            Ratio = Number(obj["ratio"]) ?? s.Ratio,
            AttackMs = Number(obj["attackMs"]) ?? s.AttackMs,
            ReleaseMs = Number(obj["releaseMs"]) ?? s.ReleaseMs,
            OutputGainDb = Number(obj["outputGainDb"]) ?? s.OutputGainDb,
            StereoLink = Flag(obj["stereoLink"]) ?? s.StereoLink,
            CeilingDb = Number(obj["ceilingDb"]) ?? s.CeilingDb,
        }).Clamped();
    }

    private static JsonArray WriteExplorerFolders(List<ExplorerFolder> folders) =>
        new(folders.Select(f => (JsonNode)new JsonObject { ["name"] = f.Name, ["path"] = f.Path }).ToArray());

    private static List<ExplorerFolder> ReadExplorerFolders(JsonNode? node)
    {
        var list = new List<ExplorerFolder>();
        if (node is not JsonArray items)
            return list;
        foreach (var item in items)
        {
            string? path = Text(item?["path"]);
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) continue;
            string name = Text(item?["name"]) is { } n && CleanProgramName(n) is { Length: > 0 } clean
                ? clean
                : ExplorerFolderDefaultName(path);
            list.Add(new ExplorerFolder(name, path));
            if (list.Count == EngineConfig.MaxExplorerFolders) break;
        }
        return list;
    }

    /// <summary>Nom per defecte d'una carpeta de l'explorador: el de la carpeta (o la unitat, p. ex. «D:\»).</summary>
    public static string ExplorerFolderDefaultName(string path)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(path);
        string name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : path;
    }

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
