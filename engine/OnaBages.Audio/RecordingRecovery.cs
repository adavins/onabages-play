using System.Text.Json;
using System.Text.Json.Nodes;

namespace OnaBages.Audio;

/// <summary>Què ha passat en intentar recuperar un enregistrament.</summary>
public enum RecoveryOutcome
{
    /// <summary>Recuperat: el fitxer és vàlid i el marcador s'ha esborrat.</summary>
    Recovered,
    /// <summary>El fitxer és obert per un altre procés (encara s'està enregistrant): no s'ha tocat res.</summary>
    InUse,
    /// <summary>El marcador hi era, però el fitxer MP3 no: s'ha esborrat el marcador.</summary>
    Missing,
    /// <summary>No s'ha pogut recuperar (no s'hi ha trobat àudio, error de disc...): el marcador es manté.</summary>
    Failed,
}

/// <summary>Resultat de recuperar un enregistrament interromput.</summary>
/// <param name="Path">Fitxer MP3.</param>
/// <param name="RecorderId">Enregistrador que el feia (del marcador; buit si no se sap).</param>
/// <param name="StartedAt">Quan va començar (del marcador; si no, la data del fitxer).</param>
/// <param name="Duration">Durada que ha quedat.</param>
/// <param name="FileSize">Mida que ha quedat.</param>
/// <param name="TrimmedBytes">Bytes retallats al final (un fotograma a mitges o brossa).</param>
/// <param name="RemovedTag">S'ha tret una etiqueta Xing/Info del principi.</param>
/// <param name="Message">Explicació en català per a l'operador.</param>
public sealed record RecoveryResult(
    string Path, RecoveryOutcome Outcome, string RecorderId, DateTimeOffset StartedAt, TimeSpan Duration,
    long FileSize, long TrimmedBytes, bool RemovedTag, string Message);

/// <summary>
/// Recuperació d'enregistraments interromputs (especificació 8.5). La fan servir el motor
/// (en arrencar) i la utilitat <c>OnaBages.Recovery.exe</c>.
///
/// Mentre s'enregistra, al costat de cada fitxer hi ha un marcador <c>fitxer.mp3.rec</c>; si el
/// motor o l'ordinador cauen, el marcador queda. Recuperar vol dir:
/// 1. retallar el fitxer fins a l'últim fotograma MP3 complet;
/// 2. treure l'etiqueta Xing/Info, si n'hi ha (els nostres fitxers no en porten, però per si de cas);
/// 3. esborrar el marcador.
/// Mai no es toca un fitxer que un altre procés té obert (un enregistrament que encara continua).
/// </summary>
public static class RecordingRecovery
{
    public const string MarkerExtension = ".rec";

    /// <summary>Marcadors (<c>*.mp3.rec</c>) que hi ha en una carpeta (sense entrar a les subcarpetes).</summary>
    public static IReadOnlyList<string> FindMarkers(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        try
        {
            return Directory.GetFiles(folder, "*.mp3" + MarkerExtension).Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Fitxer MP3 que correspon a un marcador (<c>x.mp3.rec</c> → <c>x.mp3</c>).</summary>
    public static string Mp3ForMarker(string markerPath) =>
        markerPath.EndsWith(MarkerExtension, StringComparison.OrdinalIgnoreCase)
            ? markerPath[..^MarkerExtension.Length]
            : markerPath;

    /// <summary>Recupera un enregistrament. Accepta el fitxer MP3 o el seu marcador.</summary>
    public static RecoveryResult Recover(string path)
    {
        string mp3 = Mp3ForMarker(path);
        string marker = mp3 + MarkerExtension;
        var (recorderId, startedAt) = ReadMarker(marker, mp3);

        RecoveryResult Result(RecoveryOutcome outcome, string message, TimeSpan duration = default, long size = 0,
            long trimmed = 0, bool removedTag = false) =>
            new(mp3, outcome, recorderId, startedAt, duration, size, trimmed, removedTag, message);

        if (!File.Exists(mp3))
        {
            TryDelete(marker);
            return Result(RecoveryOutcome.Missing, "El fitxer MP3 ja no hi és: s'ha esborrat el marcador.");
        }

        try
        {
            long trimmed;
            bool removedTag = false;
            Mp3Analysis analysis;

            // FileShare.None: si un altre procés el té obert (s'hi està enregistrant), falla i no es toca.
            using (var stream = new FileStream(mp3, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                analysis = Mp3Frames.Analyze(stream);
                if (analysis.Frames == 0 && stream.Length > 0)
                    return Result(RecoveryOutcome.Failed,
                        "No s'hi ha trobat cap fotograma MP3 vàlid: no s'ha tocat el fitxer (el marcador es manté).");

                // 1. Fins a l'últim fotograma complet.
                trimmed = analysis.TrailingBytes;
                if (trimmed > 0)
                {
                    stream.SetLength(analysis.ValidLength);
                    stream.Flush(flushToDisk: true);
                }
            }

            // 2. Etiqueta Xing/Info: el primer fotograma no és àudio; si diu una durada falsa, els
            //    reproductors s'hi fiarien. Es treu tot el fotograma (cal reescriure el fitxer).
            if (analysis.HasXingTag && analysis.FirstFrameLength > 0)
            {
                RemoveFrame(mp3, analysis.FirstFrameOffset, analysis.FirstFrameLength);
                removedTag = true;
                analysis = Mp3Frames.Analyze(mp3);
            }

            // 3. Ja és vàlid: el marcador sobra.
            File.Delete(marker);

            long size = new FileInfo(mp3).Length;
            var details = new List<string>();
            if (trimmed > 0) details.Add($"s'han retallat {trimmed} bytes del final");
            if (removedTag) details.Add("s'ha tret l'etiqueta Xing/Info");
            string message = details.Count == 0 ? "Recuperat (no calia retallar res)." : $"Recuperat: {string.Join(", ", details)}.";
            return Result(RecoveryOutcome.Recovered, message, analysis.Duration, size, trimmed, removedTag);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return Result(RecoveryOutcome.InUse, "El fitxer és obert per un altre programa (potser encara s'hi enregistra): no s'ha tocat.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result(RecoveryOutcome.Failed, $"No s'ha pogut recuperar: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------

    /// <summary>Dades del marcador (JSON que escriu l'enregistrador). Si no es pot llegir, el que es pugui.</summary>
    private static (string RecorderId, DateTimeOffset StartedAt) ReadMarker(string marker, string mp3)
    {
        string recorderId = "";
        DateTimeOffset? startedAt = null;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(marker)) is JsonObject o)
            {
                recorderId = o["recorder"]?.GetValue<string>() ?? "";
                if (DateTimeOffset.TryParse(o["startedAt"]?.GetValue<string>(), out var parsed)) startedAt = parsed;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // Marcador buit o malmès (p. ex. tall de corrent just en crear-lo): es fa servir la data del fitxer.
        }

        if (startedAt is null)
        {
            var created = File.Exists(mp3) ? File.GetCreationTime(mp3) : File.GetCreationTime(marker);
            startedAt = new DateTimeOffset(created);
        }
        return (recorderId, startedAt.Value);
    }

    /// <summary>
    /// Treu <paramref name="length"/> bytes a partir de <paramref name="offset"/>: copia la resta a un
    /// fitxer temporal i el substitueix (si alguna cosa falla, l'original queda igual).
    /// </summary>
    private static void RemoveFrame(string path, long offset, int length)
    {
        string temp = path + ".recuperant";
        using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            CopyRange(source, target, 0, offset);
            CopyRange(source, target, offset + length, source.Length - offset - length);
            target.Flush(flushToDisk: true);
        }
        File.Replace(temp, path, destinationBackupFileName: null);
    }

    private static void CopyRange(Stream source, Stream target, long start, long count)
    {
        var buffer = new byte[256 * 1024];
        source.Position = start;
        while (count > 0)
        {
            int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0) break;
            target.Write(buffer, 0, read);
            count -= read;
        }
    }

    /// <summary>Error de Windows 32 (ERROR_SHARING_VIOLATION) o 33 (ERROR_LOCK_VIOLATION).</summary>
    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* no és greu */ }
    }
}
