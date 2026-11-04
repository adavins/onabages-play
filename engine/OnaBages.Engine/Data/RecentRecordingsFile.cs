using System.Text.Json;
using System.Text.Json.Nodes;

namespace OnaBages.Engine.Data;

/// <summary>
/// La llista d'enregistraments recents (<c>%APPDATA%\OnaBages\enregistraments-recents.json</c>),
/// per recuperar-la en tornar a arrencar el motor. Forma part del mòdul de dades.
/// Només s'hi guarden rutes i dades; mai l'àudio.
/// </summary>
public static class RecentRecordingsFile
{
    public const int SchemaVersion = 1;

    public static string DefaultPath => Path.Combine(RuntimeFile.DirectoryPath, "enregistraments-recents.json");

    /// <summary>Desa la llista (escriptura atòmica).</summary>
    public static void Write(IEnumerable<RecordingItem> items, string? path = null)
    {
        var list = new JsonArray();
        foreach (var item in items)
        {
            list.Add(new JsonObject
            {
                ["path"] = item.Path,
                ["recorder"] = item.RecorderId,
                ["startedAt"] = item.StartedAt.ToString("O"),
                ["duration"] = Math.Round(item.Duration.TotalSeconds, 1),
                ["fileSize"] = item.FileSize,
                ["saved"] = item.Saved,
                ["closedCleanly"] = item.ClosedCleanly,
                ["recovered"] = item.Recovered,
            });
        }
        var root = new JsonObject { ["schemaVersion"] = SchemaVersion, ["items"] = list };
        AtomicFile.WriteAllText(path ?? DefaultPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Llegeix la llista. Si no hi és o no es pot llegir, retorna una llista buida
    /// (perdre la llista de recents no és greu: els fitxers continuen al disc).
    /// </summary>
    public static List<RecordingItem> Read(string? path = null)
    {
        var result = new List<RecordingItem>();
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path ?? DefaultPath)) is not JsonObject root
                || root["schemaVersion"]?.GetValue<int>() != SchemaVersion
                || root["items"] is not JsonArray items)
                return result;

            foreach (var node in items)
            {
                if (node is not JsonObject o || o["path"]?.GetValue<string>() is not { Length: > 0 } file)
                    continue;
                result.Add(new RecordingItem(
                    Id: "",
                    Path: file,
                    RecorderId: o["recorder"]?.GetValue<string>() ?? "",
                    StartedAt: DateTimeOffset.TryParse(o["startedAt"]?.GetValue<string>(), out var started) ? started : File.GetCreationTime(file),
                    Duration: TimeSpan.FromSeconds(o["duration"]?.GetValue<double>() ?? 0),
                    FileSize: o["fileSize"]?.GetValue<long>() ?? 0,
                    Saved: o["saved"]?.GetValue<bool>() ?? false,
                    Busy: false,
                    ClosedCleanly: o["closedCleanly"]?.GetValue<bool>() ?? true,
                    Problem: null,
                    // Camp afegit al pas 7 (opcional: els fitxers d'abans no el tenen; no cal canviar de versió).
                    Recovered: o["recovered"]?.GetValue<bool>() ?? false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            // No hi és o és malmès: es comença amb la llista buida.
        }
        return result;
    }
}
