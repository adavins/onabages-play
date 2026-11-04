using System.Text.Json;
using System.Text.Json.Nodes;
using OnaBages.Engine.Data;

namespace OnaBages.Engine;

/// <summary>
/// Fitxer d'execució <c>%APPDATA%\OnaBages\engine.json</c>: diu a la interfície
/// on és el motor (port i testimoni). Permet que una interfície reiniciada
/// es torni a connectar a un motor que continua funcionant.
///
/// <see cref="DirectoryPath"/> és també la carpeta de totes les dades del motor (configuració,
/// llistes, recents, historial, registres). En mode portable (D-044) és la carpeta <c>dades\</c>
/// de l'aplicació, que la interfície passa amb <c>--data-dir</c>.
/// </summary>
public static class RuntimeFile
{
    public const int SchemaVersion = 1;

    /// <summary>Carpeta de dades per defecte (aplicació no portable).</summary>
    public static string DefaultDirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OnaBages");

    /// <summary>Carpeta de dades. Només es canvia en arrencar (<see cref="UseDataDirectory"/>).</summary>
    public static string DirectoryPath { get; private set; } = DefaultDirectoryPath;

    public static string FilePath => Path.Combine(DirectoryPath, "engine.json");

    /// <summary>Fa servir una altra carpeta de dades. S'ha de cridar abans de llegir o escriure res.</summary>
    public static void UseDataDirectory(string path) => DirectoryPath = Path.GetFullPath(path);

    public static void Write(EngineOptions options)
    {
        var data = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["pid"] = Environment.ProcessId,
            ["port"] = options.Port,
            ["token"] = options.Token,
            ["startedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["version"] = EngineInfo.Version,
        };

        AtomicFile.WriteAllText(FilePath, data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Esborra el fitxer només si l'ha escrit aquest mateix procés.</summary>
    public static void DeleteIfOwned()
    {
        try
        {
            if (!File.Exists(FilePath))
                return;

            var node = JsonNode.Parse(File.ReadAllText(FilePath));
            int? pid = node?["pid"]?.GetValue<int>();
            if (pid == Environment.ProcessId)
                File.Delete(FilePath);
        }
        catch (Exception)
        {
            // Si no es pot llegir o esborrar, no és greu: la interfície
            // comprova igualment si el motor respon abans de fer-lo servir.
        }
    }
}
