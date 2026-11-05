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
/// Contingut (text): «OBLU1;lufs;pic» (lufs buit si la pista és silenci). Mai no llança excepcions.
/// </summary>
public sealed class LoudnessCacheFile(string? folder = null) : ILoudnessCache
{
    public const int KeepDays = 60;
    private const string Header = "OBLU1";

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
            if (parts.Length != 3 || parts[0] != Header) return null;
            double? lufs = parts[1].Length == 0 ? null : double.Parse(parts[1], CultureInfo.InvariantCulture);
            float peak = float.Parse(parts[2], CultureInfo.InvariantCulture);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow); // s'ha fet servir: no s'esborrarà aviat
            return new TrackLoudness(lufs, peak);
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
            string text = string.Create(CultureInfo.InvariantCulture, $"{Header};{loudness.Lufs};{loudness.Peak}");
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
