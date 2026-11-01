using System.Text.Json;
using System.Text.Json.Nodes;
using OnaBages.Engine.Data;

namespace OnaBages.Engine;

/// <summary>
/// Fitxer d'execució <c>%APPDATA%\OnaBages\engine.json</c>: diu a la interfície
/// on és el motor (port i testimoni). Permet que una interfície reiniciada
/// es torni a connectar a un motor que continua funcionant.
/// </summary>
public static class RuntimeFile
{
    public const int SchemaVersion = 1;

    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OnaBages");

    public static string FilePath { get; } = Path.Combine(DirectoryPath, "engine.json");

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
