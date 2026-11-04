using System.Security.Cryptography;
using System.Text;
using OnaBages.Audio;

namespace OnaBages.Engine.Data;

/// <summary>
/// Memòria cau de les formes d'ona de l'editor (especificació 9.3 i 13): un fitxer per MP3 a
/// <c>%APPDATA%\OnaBages\cache\formes-ona\</c>, perquè la segona vegada que s'obre un fitxer
/// sigui immediata. Forma part del mòdul de dades.
///
/// El nom del fitxer surt de la ruta, la mida i la data de l'MP3: si l'MP3 canvia, ja no es troba
/// i es torna a calcular. Els que fa més de <see cref="KeepDays"/> dies que no es fan servir s'esborren.
/// Format: «OBPK», versió (1 byte), fotogrames (int32) i els pics (<see cref="WaveformPeaks"/>).
/// </summary>
public static class PeakCacheFile
{
    public const int KeepDays = 60;
    private const byte Version = 1;
    private static readonly byte[] Magic = "OBPK"u8.ToArray();

    public static string DefaultFolder => Path.Combine(RuntimeFile.DirectoryPath, "cache", "formes-ona");

    /// <summary>Fitxer de la memòria cau per a aquest MP3 (tal com era quan es va indexar).</summary>
    public static string PathFor(Mp3FrameIndex index, string? folder = null)
    {
        string key = $"{Path.GetFullPath(index.Path).ToLowerInvariant()}|{index.FileSize}|{index.LastWriteUtc.Ticks}";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();
        return Path.Combine(folder ?? DefaultFolder, hash + ".obpk");
    }

    /// <summary>Els pics desats, o null si no n'hi ha (o no corresponen al fitxer). Mai no llança excepcions.</summary>
    public static byte[]? Read(Mp3FrameIndex index, string? folder = null)
    {
        string path = PathFor(index, folder);
        try
        {
            if (!File.Exists(path)) return null;
            byte[] data = File.ReadAllBytes(path);
            int header = Magic.Length + 1 + 4;
            if (data.Length < header || !data.AsSpan(0, 4).SequenceEqual(Magic) || data[4] != Version) return null;
            int frames = BitConverter.ToInt32(data, 5);
            if (frames != index.FrameCount || data.Length != header + frames * WaveformPeaks.BytesPerFrame) return null;

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow); // s'ha fet servir: no s'esborrarà aviat
            return data[header..];
        }
        catch (Exception)
        {
            return null; // sense memòria cau, es torna a calcular
        }
    }

    /// <summary>Desa els pics. Si no es pot, no passa res (la propera vegada es tornaran a calcular).</summary>
    public static void Write(Mp3FrameIndex index, byte[] peaks, string? folder = null)
    {
        try
        {
            using var data = new MemoryStream();
            data.Write(Magic);
            data.WriteByte(Version);
            data.Write(BitConverter.GetBytes(index.FrameCount));
            data.Write(peaks, 0, index.FrameCount * WaveformPeaks.BytesPerFrame);
            string path = PathFor(index, folder);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, data.ToArray());
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception)
        {
            // La memòria cau és només per anar més de pressa.
        }
    }

    /// <summary>Esborra les formes d'ona que fa més de <see cref="KeepDays"/> dies que no es fan servir.</summary>
    public static void DeleteOld(string? folder = null)
    {
        try
        {
            var dir = new DirectoryInfo(folder ?? DefaultFolder);
            if (!dir.Exists) return;
            var limit = DateTime.UtcNow.AddDays(-KeepDays);
            foreach (var file in dir.EnumerateFiles("*.obpk"))
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
