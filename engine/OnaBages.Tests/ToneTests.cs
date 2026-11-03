using NAudio.Wave;
using OnaBages.Audio;

namespace OnaBages.Tests;

/// <summary>Generador i analitzador del to de prova (pas 8): ha de trobar els talls i no donar falses alarmes.</summary>
public class ToneTests
{
    private const int Rate = 48000;

    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests");

    /// <summary>
    /// Simula un enregistrament: 1 s de silenci, el to, i 1 s de silenci.
    /// <paramref name="damage"/> pot espatllar el to (en mostres estèreo intercalades).
    /// </summary>
    private static float[] Recording(double toneSeconds, Func<float[], float[]>? damage = null, float gain = 1)
    {
        var tone = new float[(int)(toneSeconds * Rate) * 2];
        new ToneGenerator(Rate).Fill(tone);
        for (int i = 0; i < tone.Length; i++)
            tone[i] *= gain;
        if (damage is not null)
            tone = damage(tone);

        var silence = new float[Rate * 2];
        return [.. silence, .. tone, .. silence];
    }

    /// <summary>Insereix silenci al to (com un reproductor que es queda sense dades).</summary>
    private static Func<float[], float[]> InsertSilence(double atSeconds, double ms) => tone =>
    {
        int at = (int)(atSeconds * Rate) * 2;
        var silence = new float[(int)(ms * Rate / 1000) * 2];
        return [.. tone[..at], .. silence, .. tone[at..]];
    };

    /// <summary>Treu un tros del to (com mostres perdudes en enregistrar).</summary>
    private static Func<float[], float[]> Remove(double atSeconds, double ms) => tone =>
    {
        int at = (int)(atSeconds * Rate) * 2;
        int length = (int)(ms * Rate / 1000) * 2;
        return [.. tone[..at], .. tone[(at + length)..]];
    };

    private static string WriteMp3(float[] samples)
    {
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, $"to-{Guid.NewGuid():N}.mp3");
        using var encoder = new LameEncoder(Rate);
        using var file = File.Create(path);
        const int block = 9600;
        for (int i = 0; i < samples.Length; i += block)
        {
            int count = Math.Min(block, samples.Length - i);
            file.Write(encoder.Encode(samples[i..(i + count)], count));
        }
        file.Write(encoder.Flush());
        return path;
    }

    private static ToneReport Analyze(float[] samples)
    {
        var analyzer = new ToneAnalyzer(Rate);
        analyzer.Process(samples);
        return analyzer.Finish();
    }

    private static void AssertNear(double expectedSeconds, TimeSpan actual, double toleranceMs = 3) =>
        Assert.InRange(actual.TotalMilliseconds, expectedSeconds * 1000 - toleranceMs, expectedSeconds * 1000 + toleranceMs);

    [Fact]
    public void To_net_no_te_problemes()
    {
        var report = Analyze(Recording(5));

        Assert.True(report.Ok, string.Join(", ", report.Events));
        AssertNear(1, report.ToneStart, 1);
        AssertNear(5, report.ToneLength, 1);
        Assert.InRange(report.LevelDb, -15.1, -14.9);
    }

    [Fact]
    public void To_passat_per_mp3_no_te_problemes()
    {
        string path = WriteMp3(Recording(10));

        var report = ToneAnalyzer.AnalyzeFile(path);
        File.Delete(path);

        Assert.True(report.Ok, string.Join(", ", report.Events));
        AssertNear(10, report.ToneLength);
    }

    [Fact]
    public void To_fluix_passat_per_mp3_no_te_problemes()
    {
        string path = WriteMp3(Recording(10, gain: 0.05f)); // −41 dBFS: volum de Windows molt baix

        var report = ToneAnalyzer.AnalyzeFile(path);
        File.Delete(path);

        Assert.True(report.Ok, string.Join(", ", report.Events));
    }

    [Fact]
    public void Wav_generat_dura_el_que_toca()
    {
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, $"to-{Guid.NewGuid():N}.wav");
        ToneGenerator.WriteWav(path, TimeSpan.FromSeconds(3));

        var report = ToneAnalyzer.AnalyzeFile(path);
        using (var reader = new WaveFileReader(path))
            Assert.Equal(3 * Rate, reader.SampleCount);
        File.Delete(path);

        Assert.True(report.Ok, string.Join(", ", report.Events));
        AssertNear(3, report.ToneLength, 1);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    public void Detecta_un_silenci_enmig_del_to(double ms)
    {
        string path = WriteMp3(Recording(10, InsertSilence(4, ms)));

        var report = ToneAnalyzer.AnalyzeFile(path);
        File.Delete(path);

        var gap = Assert.Single(report.Events);
        Assert.Equal(ToneProblem.Gap, gap.Problem);
        AssertNear(4, gap.Position - report.ToneStart); // el MP3 afegeix uns ms de retard a l'inici del fitxer
        Assert.InRange(gap.Length.TotalMilliseconds, ms - 3, ms + 3);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(100)]
    public void Detecta_un_tros_perdut(double ms)
    {
        string path = WriteMp3(Recording(10, Remove(6, ms)));

        var report = ToneAnalyzer.AnalyzeFile(path);
        File.Delete(path);

        var click = Assert.Single(report.Events);
        Assert.Equal(ToneProblem.Click, click.Problem);
        AssertNear(6, click.Position - report.ToneStart);
        AssertNear(10 - ms / 1000, report.ToneLength);
        Assert.InRange(report.MissingMs(10), ms - 2, ms + 2);
    }

    [Fact]
    public void Les_ones_compten_tot_el_to()
    {
        string path = WriteMp3(Recording(10));

        var report = ToneAnalyzer.AnalyzeFile(path);
        File.Delete(path);

        Assert.InRange(report.MissingMs(10), -2, 2);
        Assert.InRange(report.ClockPpm, -100, 100);
    }

    [Fact]
    public void Una_diferencia_de_rellotges_no_es_audio_perdut()
    {
        // Com la prova real: el rellotge d'entrada va 100 ppm més lent que el de sortida.
        // Els 60 s de to (97.080 ones) queden en 59,994 s, sense cap tall.
        var tone = new float[(int)(60 * Rate * (1 - 100e-6)) * 2];
        new ToneGenerator((int)(Rate * (1 - 100e-6))).Fill(tone);
        var silence = new float[Rate * 2];

        var report = Analyze([.. silence, .. tone, .. silence]);

        Assert.True(report.Ok, string.Join(", ", report.Events));
        AssertNear(60 - 0.006, report.ToneLength, 1);
        Assert.InRange(report.MissingMs(60), -2, 2);
        Assert.InRange(report.ClockPpm, 70, 130);
    }

    [Fact]
    public void Analitza_un_mp3_tallat_a_mig_fotograma()
    {
        // Com un enregistrament després d'un tancament forçat: el to no acaba i l'últim fotograma és incomplet.
        var samples = Recording(10);
        string path = WriteMp3(samples[..((int)(7.5 * Rate) * 2)]); // 1 s de silenci + 6,5 s de to
        using (var file = new FileStream(path, FileMode.Open))
            file.SetLength(file.Length - 517);

        var report = ToneAnalyzer.AnalyzeFile(path);
        File.Delete(path);

        Assert.True(report.Ok, string.Join(", ", report.Events));
        Assert.InRange(report.ToneLength.TotalSeconds, 6.4, 6.5);
    }

    [Fact]
    public void Sense_to_ho_diu()
    {
        var report = Analyze(new float[Rate * 4]);

        Assert.False(report.ToneFound);
        Assert.False(report.Ok);
    }

    [Fact]
    public void Una_altra_cosa_que_no_es_el_to_no_dona_correcte()
    {
        // Soroll: com si s'hagués enregistrat una altra font.
        var random = new Random(1);
        var noise = new float[Rate * 2 * 3];
        for (int i = 0; i < noise.Length; i++)
            noise[i] = (float)(random.NextDouble() - 0.5) * 0.3f;

        var report = Analyze(noise);

        Assert.False(report.Ok);
        Assert.True(report.TooManyEvents);
    }
}
