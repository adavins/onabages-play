using System.Text;

namespace OnaBages.Engine.Data;

/// <summary>
/// Historial de reproducció (especificació 6.6): un fitxer CSV per dia a
/// <c>%APPDATA%\OnaBages\historial\historial-AAAA-MM-DD.csv</c>. Forma part del mòdul de dades.
///
/// Format pensat per obrir-lo amb l'Excel en català: separador «;», UTF-8 amb BOM
/// (perquè surtin bé els accents) i durades com a hh:mm:ss.
/// Cada línia s'afegeix al final i es força al disc: si l'ordinador cau, només es pot perdre la que s'escrivia.
/// </summary>
public static class PlayHistoryFile
{
    public static string DefaultFolder => Path.Combine(RuntimeFile.DirectoryPath, "historial");

    public const string Header = "data;hora;programa;reproductor;títol;artista;fitxer;sonat;durada";

    private static readonly Lock Sync = new();

    /// <summary>Fitxer del dia en què va començar la pista.</summary>
    public static string PathFor(DateTime day, string? folder = null) =>
        System.IO.Path.Combine(folder ?? DefaultFolder, $"historial-{day:yyyy-MM-dd}.csv");

    /// <summary>Afegeix una pista a l'historial del dia. Si el fitxer no existeix, el crea amb la capçalera.</summary>
    /// <exception cref="IOException">Si no s'hi pot escriure (disc ple, fitxer obert i bloquejat...).</exception>
    public static void Append(PlayedTrack track, string? title, string? artist, string? folder = null)
    {
        string line = string.Join(';',
            track.StartedAt.ToString("yyyy-MM-dd"),
            track.StartedAt.ToString("HH:mm:ss"),
            Field(track.Program),
            Field(track.PlayerId),
            Field(title),
            Field(artist),
            Field(track.FilePath),
            Time(track.Played),
            Time(track.Duration));

        string path = PathFor(track.StartedAt, folder);
        lock (Sync)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            bool isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
            // FileShare.ReadWrite: es pot tenir obert amb l'Excel mentre s'hi afegeixen línies (si l'Excel no el bloqueja).
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            string text = (isNew ? Header + "\r\n" : "") + line + "\r\n";
            byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
            if (isNew) stream.Write(Encoding.UTF8.Preamble);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
    }

    /// <summary>Camp de text: entre cometes si conté «;», cometes o salts de línia (format CSV habitual).</summary>
    public static string Field(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.IndexOfAny([';', '"', '\r', '\n']) < 0) return text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private static string Time(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
}
