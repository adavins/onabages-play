using System.Text;
using System.Text.Json.Nodes;

namespace OnaBages.Audio;

/// <summary>Una marca d'un enregistrament (fase 5, pas 23a; D-110).</summary>
/// <param name="Seconds">Posició dins de l'enregistrament, en segons des del principi.</param>
/// <param name="Name">Nom de la marca (opcional; de moment, sempre buit).</param>
public sealed record RecordingMark(double Seconds, string? Name = null);

/// <summary>
/// Fitxer de marques d'un enregistrament: <c>Nom.mp3.marques</c>, al costat del MP3 (fase 5, pas 23a; D-110).
/// JSON amb <c>schemaVersion</c>. Les marques no van dins del MP3 perquè el fitxer de l'enregistrament
/// no s'ha de tocar mai mentre s'enregistra: si aquest fitxer es perd o es fa malbé, l'àudio no en pateix.
///
/// Escriptura segura (temporal + substitució). No es crida mai des del fil d'àudio.
/// </summary>
public static class RecordingMarks
{
    public const string Extension = ".marques";
    public const int SchemaVersion = 1;

    // Accents i apòstrofs tal qual ("Notícies", no "Notícies"): el fitxer es pot llegir i no va a cap web.
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>On són les marques de <paramref name="mp3Path"/>.</summary>
    public static string PathFor(string mp3Path) => mp3Path + Extension;

    /// <summary>Desa les marques al costat de <paramref name="mp3Path"/> (les substitueix totes).</summary>
    public static void Write(string mp3Path, IReadOnlyList<RecordingMark> marks)
    {
        var list = new JsonArray();
        foreach (var mark in marks)
        {
            var node = new JsonObject { ["seconds"] = Math.Round(mark.Seconds, 3) };
            if (!string.IsNullOrEmpty(mark.Name)) node["name"] = mark.Name;
            list.Add(node);
        }
        var root = new JsonObject { ["schemaVersion"] = SchemaVersion, ["marks"] = list };

        string path = PathFor(mp3Path);
        string tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(root.ToJsonString(JsonOptions));
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Les marques de <paramref name="mp3Path"/>, ordenades. Si no n'hi ha o el fitxer no es pot llegir, cap
    /// (mai no falla: unes marques malmeses no han d'impedir obrir l'àudio).
    /// </summary>
    public static List<RecordingMark> Read(string mp3Path)
    {
        var marks = new List<RecordingMark>();
        try
        {
            string path = PathFor(mp3Path);
            if (!File.Exists(path)) return marks;
            if (JsonNode.Parse(File.ReadAllText(path))?["marks"] is not JsonArray list) return marks;
            foreach (var node in list)
            {
                if (node?["seconds"] is JsonValue v && v.TryGetValue(out double s) && double.IsFinite(s) && s >= 0)
                    marks.Add(new RecordingMark(s, node["name"] is JsonValue n && n.TryGetValue(out string? name) ? name : null));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        marks.Sort((a, b) => a.Seconds.CompareTo(b.Seconds));
        return marks;
    }

    /// <summary>
    /// On queden les marques a la versió editada (fase 5, pas 23c): els trossos de l'original que es queden són
    /// <paramref name="segments"/>. Les que eren en un tros eliminat no hi surten. Cada una conserva el número que
    /// tenia (1, 2, 3... de l'original), perquè no canviïn de nom en retallar.
    /// </summary>
    /// <param name="originalFrames">Fotogrames de l'original sencer.</param>
    public static List<(int Number, RecordingMark Mark)> MapToEdited(
        IReadOnlyList<RecordingMark> marks, IReadOnlyList<FrameRange> segments, double frameSeconds, int originalFrames)
    {
        var result = new List<(int, RecordingMark)>();
        if (frameSeconds <= 0) return result;
        for (int i = 0; i < marks.Count; i++)
        {
            double frame = marks[i].Seconds / frameSeconds; // fotograma de l'original (amb decimals)
            // Més enllà del final (pas 24b: una marca posada després de la fotografia d'un enregistrament en curs): no hi és.
            if (frame > originalFrames) continue;
            // Una marca just al final del fitxer és de l'últim fotograma.
            int whole = Math.Min((int)Math.Floor(frame), Math.Max(0, originalFrames - 1));
            long before = 0; // fotogrames de la versió editada abans del tros
            foreach (var s in segments)
            {
                if (whole >= s.Start && whole < s.Start + s.Count)
                {
                    double edited = before + Math.Min(frame - s.Start, s.Count);
                    result.Add((i + 1, marks[i] with { Seconds = edited * frameSeconds }));
                    break;
                }
                before += s.Count;
            }
        }
        return result;
    }

    /// <summary>
    /// L'MP3 s'ha mogut de <paramref name="fromMp3"/> a <paramref name="toMp3"/>: les marques el segueixen.
    /// Si no n'hi ha, no fa res. Si a la destinació ja n'hi havia unes d'un altre fitxer (que ja no hi és), se substitueixen.
    /// </summary>
    public static void Move(string fromMp3, string toMp3)
    {
        string from = PathFor(fromMp3);
        if (File.Exists(from)) File.Move(from, PathFor(toMp3), overwrite: true);
    }
}
