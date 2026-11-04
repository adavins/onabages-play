using OnaBages.Audio;
using Xunit.Abstractions;

namespace OnaBages.Tests;

/// <summary>
/// Fitxers que se substitueixen mentre sonen (Ona Bages 24h, pas 2; docs/notes-ona-bages-24h.md, 2).
/// Què passa si un altre programa esborra, reanomena o substitueix un fitxer que
/// <see cref="AudioFileSource"/> està llegint? Es prova amb un WAV (lector propi de NAudio)
/// i amb un MP3 (Media Foundation de Windows), que són els dos camins de lectura.
///
/// Aquestes proves <b>documenten</b> el comportament actual; no el canvien. Si algun dia es canvia
/// la manera d'obrir els fitxers, s'hauran d'actualitzar els resultats esperats.
/// </summary>
public class FileLockTests(ITestOutputHelper output) : IDisposable
{
    private const int Rate = 48000;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "lock-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Fitxer de prova (WAV o MP3, segons l'extensió) a la carpeta de la prova.</summary>
    private string CreateFile(string name, double seconds)
    {
        Directory.CreateDirectory(_folder);
        string wav = TestAudio.CreateWav(Rate, 2, seconds);
        string path = Path.Combine(_folder, name);
        if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            File.Move(wav, path);
            return path;
        }

        using var source = AudioFileSource.Open(wav, Rate);
        using var encoder = new LameEncoder(Rate, 192);
        using var file = File.Create(path);
        var buffer = new float[Rate];
        int read;
        while ((read = source.Read(buffer)) > 0)
            file.Write(encoder.Encode(buffer, read));
        file.Write(encoder.Flush());
        return path;
    }

    /// <summary>Les operacions que pot fer un altre programa (p. ex. l'Automatitzador de 24h).</summary>
    private static void Do(string operation, string path, string replacement)
    {
        switch (operation)
        {
            case "esborrar":
                File.Delete(path);
                break;
            case "reanomenar":
                // Treure el vell del mig i posar-hi el nou amb el mateix nom.
                File.Move(path, path + ".vell");
                File.Move(replacement, path);
                break;
            case "substituir":
                // Escriptura segura: el nou ja és complet i ocupa el lloc del vell d'un sol cop.
                File.Move(replacement, path, overwrite: true);
                break;
            default: // "sobreescriure": copiar a sobre (escriu dins del mateix fitxer)
                File.Copy(replacement, path, overwrite: true);
                break;
        }
    }

    /// <summary>
    /// Obre una pista de 3 s, en llegeix 1 s (com si sonés), fa l'operació i intenta llegir la resta.
    /// Retorna «bloquejat» si Windows no ha deixat fer l'operació, o «ok» si l'ha deixat fer i
    /// la pista que sonava s'ha pogut llegir sencera i sense errors.
    /// </summary>
    private string WhilePlaying(string extension, string operation)
    {
        string path = CreateFile("pista" + extension, 3);
        // El fitxer nou és diferent (1 s, no 3) per veure si la lectura s'hi barrejaria.
        string replacement = CreateFile("nou" + extension, 1);

        using var source = AudioFileSource.Open(path, Rate);
        var buffer = new float[Rate * 2]; // 1 s en estèreo
        long total = source.Read(buffer);

        try
        {
            Do(operation, path, replacement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"{extension} {operation}: BLOQUEJAT ({ex.Message})");
            return "bloquejat";
        }

        int read;
        while ((read = source.Read(buffer)) > 0) total += read;
        double seconds = total / 2.0 / Rate;
        output.WriteLine($"{extension} {operation}: es pot; la pista que sonava s'ha llegit {seconds:0.00} s de 3");
        Assert.InRange(seconds, 2.9, 3.1); // la pista que sonava acaba sencera, no es barreja amb la nova
        return "ok";
    }

    // --- WAV: el lector de NAudio obre el fitxer sense deixar escriure-hi ni esborrar-lo ---

    [Theory]
    [InlineData("esborrar")]
    [InlineData("reanomenar")]
    [InlineData("substituir")]
    [InlineData("sobreescriure")]
    public void Un_wav_que_sona_queda_bloquejat(string operation)
    {
        Assert.Equal("bloquejat", WhilePlaying(".wav", operation));
    }

    // --- MP3 (i la resta de formats de Media Foundation): es pot treure del mig, però no escriure-hi a sobre ---

    [Theory]
    [InlineData("esborrar")]
    [InlineData("reanomenar")]
    public void Un_mp3_que_sona_es_pot_treure_del_mig_i_continua_sonant(string operation)
    {
        Assert.Equal("ok", WhilePlaying(".mp3", operation));
    }

    /// <summary>Escriure-hi a sobre, o reanomenar-ne un altre a sobre d'un sol cop, no es pot: cal treure'l del mig abans.</summary>
    [Theory]
    [InlineData("sobreescriure")]
    [InlineData("substituir")]
    public void Un_mp3_que_sona_no_es_pot_substituir_d_un_sol_cop(string operation)
    {
        Assert.Equal("bloquejat", WhilePlaying(".mp3", operation));
    }

    [Theory]
    [InlineData(".wav")]
    [InlineData(".mp3")]
    public void Un_cop_tancat_ja_es_pot_sobreescriure(string extension)
    {
        string path = CreateFile("pista" + extension, 3);
        string replacement = CreateFile("nou" + extension, 1);
        using (var source = AudioFileSource.Open(path, Rate))
            source.Read(new float[Rate]);

        File.Copy(replacement, path, overwrite: true); // no llança cap error
    }
}
