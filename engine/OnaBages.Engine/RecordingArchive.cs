using System.Globalization;

namespace OnaBages.Engine;

/// <summary>
/// Historial d'enregistraments (fase 5, pas 10a; D-086): una còpia de cada enregistrament original, per si
/// es descarta per error (la paperera també es pot buidar) o es desa a sobre des de l'editor. La còpia l'escriu
/// l'enregistrador mentre enregistra (<see cref="OnaBages.Audio.Recorder.SetArchive"/>), en una carpeta per dia
/// (<c>AAAA-MM-DD</c>). Aquí només hi ha la carpeta per defecte i la neteja dels dies antics.
/// </summary>
public static class RecordingArchive
{
    /// <summary>Dies que es guarden per defecte.</summary>
    public const int DefaultKeepDays = 30;

    /// <summary>Màxim de dies que es poden triar (0 = no esborrar mai).</summary>
    public const int MaxKeepDays = 3650;

    /// <summary>Carpeta per defecte: Documents\Ona Bages Play\Historial d'enregistraments.</summary>
    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Ona Bages Play", "Historial d'enregistraments");

    /// <summary>
    /// Esborra els MP3 de les carpetes de dia que tenen més de <paramref name="keepDays"/> dies (0 = cap).
    /// Només toca carpetes amb nom de data (<c>AAAA-MM-DD</c>) i, dins, fitxers <c>.mp3</c>: res més. Mai el dia
    /// d'avui ni un fitxer obert (un enregistrament en curs). Una carpeta que queda buida s'esborra.
    /// Mai no llança excepcions. Retorna quants fitxers s'han esborrat.
    /// </summary>
    public static int DeleteOld(string folder, int keepDays, DateTime today)
    {
        if (keepDays <= 0) return 0;
        int deleted = 0;
        try
        {
            if (!Directory.Exists(folder)) return 0;
            var limit = today.Date.AddDays(-keepDays);
            foreach (var day in Directory.EnumerateDirectories(folder))
            {
                if (!DateTime.TryParseExact(Path.GetFileName(day), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    || date >= limit || date >= today.Date)
                    continue;
                foreach (var file in Directory.EnumerateFiles(day, "*.mp3"))
                {
                    try
                    {
                        File.Delete(file);
                        deleted++;
                    }
                    catch (IOException) { /* obert: es tornarà a provar la propera vegada */ }
                    catch (UnauthorizedAccessException) { }
                }
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(day).Any()) Directory.Delete(day);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception)
        {
            // Unitat desconnectada, sense permís...: no és greu, es tornarà a provar en arrencar.
        }
        return deleted;
    }
}
