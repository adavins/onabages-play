using OnaBages.Audio;

namespace OnaBages.Tests;

/// <summary>Proves de la cadena d'àudio, sense targeta de so.</summary>
public class AudioChainTests
{
    // --- Memòria intermèdia ---

    [Fact]
    public void Memoria_escriu_i_llegeix_donant_la_volta()
    {
        var ring = new SampleRingBuffer(8);
        var output = new float[8];

        Assert.Equal(6, ring.Write([1, 2, 3, 4, 5, 6]));
        Assert.Equal(4, ring.Read(output.AsSpan(0, 4)));        // llegeix 1..4
        Assert.Equal(6, ring.Write([7, 8, 9, 10, 11, 12]));     // dona la volta
        Assert.Equal(8, ring.Count);
        Assert.Equal(0, ring.Write([99]));                       // plena: no hi cap res més

        Assert.Equal(8, ring.Read(output));
        Assert.Equal([5f, 6, 7, 8, 9, 10, 11, 12], output);
        Assert.Equal(0, ring.Read(output));                      // buida
    }

    [Fact]
    public void Memoria_entre_dos_fils_no_perd_ni_desordena_mostres()
    {
        const int total = 2_000_000;
        var ring = new SampleRingBuffer(4096);

        var writer = Task.Run(() =>
        {
            var chunk = new float[1000];
            int next = 0;
            while (next < total)
            {
                int n = Math.Min(chunk.Length, total - next);
                for (int i = 0; i < n; i++) chunk[i] = next + i;
                int written = ring.Write(chunk.AsSpan(0, n));
                next += written;
                if (written == 0) Thread.Yield();
            }
        });

        var buffer = new float[777];
        int expected = 0;
        while (expected < total)
        {
            int read = ring.Read(buffer);
            for (int i = 0; i < read; i++)
            {
                Assert.Equal(expected, (int)buffer[i]);
                expected++;
            }
            if (read == 0) Thread.Yield();
        }

        writer.Wait();
    }

    // --- Lectura de fitxers ---

    [Fact]
    public void Fitxer_estereo_a_la_mateixa_frequencia_es_llegeix_sencer()
    {
        string path = TestAudio.CreateWav(48000, 2, 1.0);
        using var source = AudioFileSource.Open(path, 48000);

        float[] samples = TestAudio.ReadAll(source);

        Assert.Equal(48000 * 2, samples.Length);
        Assert.Equal(TimeSpan.FromSeconds(1), source.Duration);
    }

    [Fact]
    public void Fitxer_mono_es_converteix_a_estereo_amb_els_dos_canals_iguals()
    {
        string path = TestAudio.CreateWav(48000, 1, 0.5, floatFormat: false);
        using var source = AudioFileSource.Open(path, 48000);

        float[] samples = TestAudio.ReadAll(source);

        Assert.Equal(24000 * 2, samples.Length);
        for (int i = 0; i < samples.Length; i += 2)
            Assert.Equal(samples[i], samples[i + 1]);
        Assert.True(samples.Max() > 0.09f); // hi ha so, no silenci
    }

    [Fact]
    public void Fitxer_a_44100_es_remostreja_a_48000()
    {
        string path = TestAudio.CreateWav(44100, 2, 1.0);
        using var source = AudioFileSource.Open(path, 48000);

        Assert.Equal(48000, source.SampleRate);
        int frames = TestAudio.ReadAll(source).Length / 2;

        // El remostrejador pot deixar alguna mostra de més o de menys als extrems.
        Assert.InRange(frames, 47900, 48100);
    }

    [Fact]
    public void Fitxer_de_6_canals_fa_servir_els_dos_primers()
    {
        string path = TestAudio.CreateWav(48000, 6, 0.2);
        using var source = AudioFileSource.Open(path, 48000);

        float[] samples = TestAudio.ReadAll(source);

        Assert.Equal(9600 * 2, samples.Length);
        // Al fitxer, el canal 2 té el doble d'amplitud que el canal 1 (i els altres, més).
        for (int i = 0; i < samples.Length; i += 2)
            Assert.Equal(samples[i] * 2, samples[i + 1], 4);
        Assert.True(samples.Max() > 0.19f); // hi ha so
    }

    [Fact]
    public void Es_pot_començar_a_llegir_a_mig_fitxer()
    {
        string path = TestAudio.CreateWav(48000, 2, 1.0);
        using var source = AudioFileSource.Open(path, 48000, TimeSpan.FromSeconds(0.25));

        int frames = TestAudio.ReadAll(source).Length / 2;

        Assert.Equal(36000, frames); // en queden 0,75 s
        Assert.Equal(TimeSpan.FromSeconds(1), source.Duration);
    }

    [Fact]
    public void Fitxer_inexistent_dona_error_clar()
    {
        var ex = Assert.Throws<AudioFileException>(() => AudioFileSource.Open(@"C:\no\existeix.mp3", 48000));
        Assert.Contains("No es troba", ex.Message);
    }

    [Fact]
    public void Fitxer_malmes_dona_error_clar()
    {
        string path = Path.Combine(Path.GetTempPath(), $"malmes-{Guid.NewGuid():N}.mp3");
        File.WriteAllText(path, "això no és un mp3");

        var ex = Assert.Throws<AudioFileException>(() => AudioFileSource.Open(path, 48000));
        Assert.Contains("No s'ha pogut obrir", ex.Message);
    }

    // --- Fil d'àudio ---

    [Fact]
    public void Sense_dades_envia_silenci_i_compta_un_tall()
    {
        var ring = new SampleRingBuffer(1000);
        var stream = new PlayerStream(ring, 48000, 2);
        ring.Write([0.5f, 0.5f]); // una sola mostra estèreo

        var buffer = new byte[4 * 2 * sizeof(float)]; // en demana 4
        Assert.Equal(buffer.Length, stream.Read(buffer));

        var samples = new float[8];
        Buffer.BlockCopy(buffer, 0, samples, 0, buffer.Length);
        Assert.Equal([0.5f, 0.5f, 0, 0, 0, 0, 0, 0], samples);
        Assert.Equal(1, stream.Underruns);
        Assert.False(stream.EndReached);
    }

    [Fact]
    public void Al_final_del_fitxer_no_es_compta_tall_sino_final()
    {
        var ring = new SampleRingBuffer(1000);
        var stream = new PlayerStream(ring, 48000, 2);
        ring.Write([0.5f, 0.5f]);
        stream.MarkSourceFinished();

        stream.Read(new byte[4 * 2 * sizeof(float)]);

        Assert.Equal(0, stream.Underruns);
        Assert.True(stream.EndReached);
        Assert.Equal(1, stream.FramesPlayed);
    }

    [Fact]
    public void Sortida_de_6_canals_rep_l_estereo_als_dos_primers()
    {
        var ring = new SampleRingBuffer(1000);
        var stream = new PlayerStream(ring, 48000, 6);
        ring.Write([0.1f, 0.2f, 0.3f, 0.4f]);

        var buffer = new byte[2 * 6 * sizeof(float)];
        stream.Read(buffer);

        var samples = new float[12];
        Buffer.BlockCopy(buffer, 0, samples, 0, buffer.Length);
        Assert.Equal([0.1f, 0.2f, 0, 0, 0, 0, 0.3f, 0.4f, 0, 0, 0, 0], samples);
    }
}
