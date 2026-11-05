using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OnaBages.Audio;

namespace OnaBages.Engine.Data;

/// <summary>
/// Memòria cau de la sonoritat de les pistes (fase 4, pas 5b; D-071): un fitxer petit per pista a
/// <c>cache\sonoritat\</c>, perquè una pista que ja s'ha fet servir no s'hagi de tornar a mesurar.
/// Forma part del mòdul de dades.
///
/// El nom surt de la ruta, la mida i la data del fitxer d'àudio: si el fitxer canvia, ja no es troba i
/// es torna a mesurar. Els que fa més de <see cref="KeepDays"/> dies que no es fan servir s'esborren.
/// Contingut (text): «OBLU3;lufs;pic;so» (lufs buit si la pista és silenci). «so» (retall de silencis,
/// fase 5) és una llista «llindar:inici:final» separada per «|», amb els segons amb 6 decimals.
/// Les mesures de versions anteriors (sense «so», o amb els espetecs comptats) no es fan servir: es tornen a mesurar. Mai no llança excepcions.
/// </summary>
public sealed class LoudnessCacheFile(string? folder = null) : ILoudnessCache
{
    public const int KeepDays = 60;
    private const string Header = "OBLU3"; // 3: sense els espetecs aïllats (D-082)

    public static string DefaultFolder => Path.Combine(RuntimeFile.DirectoryPath, "cache", "sonoritat");

    private string Folder => folder ?? DefaultFolder;

    private string? PathFor(string audioPath)
    {
        try
        {
            var info = new FileInfo(audioPath);
            if (!info.Exists) return null;
            string key = $"{Path.GetFullPath(audioPath).ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();
            return Path.Combine(Folder, hash + ".lufs");
        }
        catch (Exception)
        {
            return null;
        }
    }

    public TrackLoudness? Read(string audioPath)
    {
        try
        {
            string? path = PathFor(audioPath);
            if (path is null || !File.Exists(path)) return null;
            string[] parts = File.ReadAllText(path).Trim().Split(';');
            if (parts.Length != 4 || parts[0] != Header) return null;
            double? lufs = parts[1].Length == 0 ? null : double.Parse(parts[1], CultureInfo.InvariantCulture);
            float peak = float.Parse(parts[2], CultureInfo.InvariantCulture);
            var sound = new List<SoundBounds>();
            foreach (string item in parts[3].Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] b = item.Split(':');
                sound.Add(new SoundBounds(
                    int.Parse(b[0], CultureInfo.InvariantCulture),
                    double.Parse(b[1], CultureInfo.InvariantCulture),
                    double.Parse(b[2], CultureInfo.InvariantCulture)));
            }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow); // s'ha fet servir: no s'esborrarà aviat
            return new TrackLoudness(lufs, peak, sound);
        }
        catch (Exception)
        {
            return null; // es torna a mesurar
        }
    }

    public void Write(string audioPath, TrackLoudness loudness)
    {
        try
        {
            string? path = PathFor(audioPath);
            if (path is null) return;
            Directory.CreateDirectory(Folder);
            string sound = string.Join('|', (loudness.Sound ?? []).Select(b =>
                string.Create(CultureInfo.InvariantCulture, $"{b.ThresholdDb}:{b.Start:0.000000}:{b.End:0.000000}")));
            string text = string.Create(CultureInfo.InvariantCulture, $"{Header};{loudness.Lufs};{loudness.Peak};{sound}");
            string temp = path + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception)
        {
            // La memòria cau és només per anar més de pressa.
        }
    }

    /// <summary>Esborra les mesures que fa més de <see cref="KeepDays"/> dies que no es fan servir.</summary>
    public void DeleteOld()
    {
        try
        {
            var dir = new DirectoryInfo(Folder);
            if (!dir.Exists) return;
            var limit = DateTime.UtcNow.AddDays(-KeepDays);
            foreach (var file in dir.EnumerateFiles("*.lufs"))
            {
                if (file.LastWriteTimeUtc < limit)
                {
                    try { file.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }
        catch (Exception)
        {
            // No és important.
        }
    }
}
