using NAudio.Wave;
using OnaBages.Audio;

namespace OnaBages.Tests;

/// <summary>
/// Detecció del so per al retall de silencis (fase 5, pas 8a; D-079). Amb senyals preparats es comprova
/// que el començament i el final del so es troben a la mostra exacta i que no se'n «menja» res: atacs
/// secs, fosos molt lents, cues que s'apaguen, soroll de fons, mono, 44,1 kHz i un MP3 real.
/// </summary>
public class SilenceDetectionTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "silencis-" + Guid.NewGuid().ToString("N"));

    public SilenceDetectionTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static float Db(double db) => (float)Math.Pow(10, db / 20);

    /// <summary>WAV en coma flotant amb el valor de cada mostra (els canals iguals, o només l'esquerre).</summary>
    private string Wav(int rate, double seconds, Func<double, float> value, int channels = 2, bool leftOnly = false)
    {
        string path = Path.Combine(_folder, $"s-{Guid.NewGuid():N}.wav");
        int frames = (int)Math.Round(rate * seconds);
        var samples = new float[frames * channels];
        for (int f = 0; f < frames; f++)
        {
            float v = value((double)f / rate);
            for (int c = 0; c < channels; c++)
                samples[f * channels + c] = leftOnly && c > 0 ? 0f : v;
        }
        using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, channels)))
            writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private static SoundBounds Bounds(string path, int thresholdDb = -60) =>
        TrackLoudness.Measure(path).SoundFor(thresholdDb) ?? throw new Xunit.Sdk.XunitException("No s'hi ha trobat so");

    [Fact]
    public void Un_atac_sec_es_troba_a_la_mostra_exacta()
    {
        // 1 s de silenci, 1 s de so que comença de cop, 1 s de silenci.
        string path = Wav(48000, 3, t => t is >= 1 and < 2 ? 0.5f : 0f);

        var b = Bounds(path);

        Assert.Equal(1.0, b.Start, 1.0 / 48000);
        Assert.Equal(2.0, b.End, 1.0 / 48000);
    }

    [Fact]
    public void Tambe_a_44_1_kHz()
    {
        string path = Wav(44100, 3, t => t is >= 0.75 and < 2.25 ? 0.3f : 0f);

        var b = Bounds(path);

        Assert.Equal(0.75, b.Start, 1.0 / 44100);
        Assert.Equal(2.25, b.End, 1.0 / 44100);
    }

    [Fact]
    public void Un_fos_dentrada_molt_lent_no_es_menja_res_per_sobre_del_llindar()
    {
        // De −80 dB a 0 dB en 2 s (40 dB per segon): passa de −60 dB a 0,5 s i de −70 dB a 0,25 s.
        string path = Wav(48000, 3, t => t < 2 ? Db(-80 + 40 * t) : 1f);

        Assert.Equal(0.5, Bounds(path, -60).Start, 0.001);
        Assert.Equal(0.25, Bounds(path, -70).Start, 0.001);
    }

    [Fact]
    public void Una_cua_que_sapaga_a_poc_a_poc_es_conserva_fins_al_llindar()
    {
        // So a −6 dB fins a 1 s i després baixa 60 dB per segon: travessa −40 a 1,567 s i −60 a 1,9 s.
        string path = Wav(48000, 3, t => t < 1 ? Db(-6) : Db(-6 - 60 * (t - 1)));

        Assert.Equal(1.9, Bounds(path, -60).End, 0.001);
        Assert.Equal(1.0 + 34.0 / 60, Bounds(path, -40).End, 0.001);
    }

    [Fact]
    public void Amb_soroll_de_fons_per_sobre_del_llindar_no_es_retalla_res()
    {
        // Soroll (un to fluix) a −50 dB tot el fitxer i so fort al mig.
        string path = Wav(48000, 3, t => (t is >= 1 and < 2 ? 0.5f : 0f) + Db(-50) * (float)Math.Sin(2 * Math.PI * 100 * t));

        var at60 = Bounds(path, -60);
        var at40 = Bounds(path, -40);

        Assert.True(at60.Start < 0.01 && at60.End > 2.99, "amb el llindar a −60, el soroll compta com a so");
        Assert.Equal(1.0, at40.Start, 0.001);                // amb el llindar a −40, només el so fort
    }

    [Fact]
    public void Un_fitxer_tot_silenci_no_te_cap_tros_de_so()
    {
        string path = Wav(48000, 2, _ => 0f);

        var analysis = TrackLoudness.Measure(path);

        Assert.Empty(analysis.Sound!);
        Assert.Null(analysis.SoundFor(-60));
    }

    [Fact]
    public void El_so_dun_sol_canal_tambe_compta()
    {
        string path = Wav(48000, 3, t => t is >= 0.5 and < 2.5 ? 0.2f : 0f, leftOnly: true);

        var b = Bounds(path);

        Assert.Equal(0.5, b.Start, 1.0 / 48000);
        Assert.Equal(2.5, b.End, 1.0 / 48000);
    }

    [Fact]
    public void Un_fitxer_mono()
    {
        string path = Wav(48000, 3, t => t is >= 1.25 and < 2 ? 0.2f : 0f, channels: 1);

        var b = Bounds(path);

        Assert.Equal(1.25, b.Start, 1.0 / 48000);
        Assert.Equal(2.0, b.End, 1.0 / 48000);
    }

    [Fact]
    public void Un_MP3_real_amb_silenci_al_principi_i_al_final()
    {
        // 0,5 s de silenci, 1 s de to, 0,5 s de silenci, codificat amb LAME a 320 kbps.
        string path = Path.Combine(_folder, "to.mp3");
        using (var encoder = new LameEncoder(48000, 320))
        using (var file = File.Create(path))
        {
            var buffer = new float[48000 * 2 * 2];
            for (int f = 0; f < 48000 * 2; f++)
            {
                double t = f / 48000.0;
                buffer[f * 2] = buffer[f * 2 + 1] = t is >= 0.5 and < 1.5 ? 0.5f * (float)Math.Sin(2 * Math.PI * 440 * t) : 0f;
            }
            file.Write(encoder.Encode(buffer, buffer.Length));
            file.Write(encoder.Flush());
        }

        var b = Bounds(path);

        // El descodificador hi afegeix uns mil·lisegons de retard (el del codificador): el so pot començar
        // una mica més tard i, per la precodificació, uns pocs mil·lisegons abans, però mai molt més aviat.
        Assert.InRange(b.Start, 0.48, 0.56);
        Assert.InRange(b.End, 1.5, 1.58);
    }
}
