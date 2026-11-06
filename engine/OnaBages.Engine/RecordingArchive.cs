using System.Globalization;

namespace OnaBages.Engine;

/// <summary>
/// Historial d'enregistraments (fase 5, pas 10a; D-086): una còpia de cada enregistrament original, per si
/// es descarta per error (la paperera també es pot buidar) o es desa a sobre des de l'editor. La còpia l'escriu
/// l'enregistrador mentre enregistra (<see cref="OnaBages.Audio.Recorder.SetArchive"/>), en una carpeta per perfil i,
/// dins, una per dia: <c>Historial d'enregistraments\La Central\AAAA-MM-DD</c> (D-088). Aquí hi ha la carpeta per
/// defecte, la del perfil i la neteja dels dies antics.
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
    /// Carpeta de l'historial d'un perfil: <c>arrel\nom del perfil</c>, sense els caràcters que Windows no accepta
    /// en un nom de carpeta (es canvien per «_»).
    /// </summary>
    public static string ProfileFolder(string root, string profileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new string(profileName.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.').Trim();
        return Path.Combine(root, safe.Length == 0 ? "Perfil" : safe);
    }

    /// <summary>
    /// Esborra els MP3 de les carpetes de dia que tenen més de <paramref name="keepDays"/> dies (0 = cap), de tots els
    /// perfils (<c>arrel\perfil\AAAA-MM-DD</c>) i també les que hi hagi directament a l'arrel (les de la primera
    /// versió, sense perfil). Només toca carpetes amb nom de data i, dins, fitxers <c>.mp3</c>: res més.
    /// Mai el dia d'avui ni un fitxer obert (un enregistrament en curs). Una carpeta de dia que queda buida s'esborra.
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
            foreach (var sub in Directory.EnumerateDirectories(folder))
            {
                if (IsDay(sub))
                {
                    deleted += DeleteDayIfOld(sub, limit, today);
                    continue;
                }
                // Carpeta d'un perfil: les seves carpetes de dia (més endins, res).
                try
                {
                    foreach (var day in Directory.EnumerateDirectories(sub))
                        deleted += DeleteDayIfOld(day, limit, today);
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

    private static bool IsDay(string path) => DayOf(path) is not null;

    private static DateTime? DayOf(string path) =>
        DateTime.TryParseExact(Path.GetFileName(path), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>Si <paramref name="day"/> és una carpeta de dia anterior a <paramref name="limit"/>, n'esborra els MP3.</summary>
    private static int DeleteDayIfOld(string day, DateTime limit, DateTime today)
    {
        if (DayOf(day) is not { } date || date >= limit || date >= today.Date) return 0;
        int deleted = 0;
        try
        {
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
            if (!Directory.EnumerateFileSystemEntries(day).Any()) Directory.Delete(day);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return deleted;
    }
}
