using System.Text;

namespace OnaBages.Engine.Data;

/// <summary>
/// Escriptura segura de fitxers: primer s'escriu un fitxer temporal, es força
/// al disc i després substitueix l'original. Un tancament inesperat mai no deixa
/// el fitxer a mitges.
///
/// Aquest serà el fonament del mòdul de dades (repositori) de la configuració.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tmp, path, overwrite: true);
    }
}
