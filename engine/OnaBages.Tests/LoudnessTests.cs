using OnaBages.Audio;

namespace OnaBages.Tests;

/// <summary>
/// Mesurador de sonoritat (fase 4, pas 5a; D-070), amb els senyals de prova de l'EBU Tech 3341
/// (casos 1, 3 i 5): el resultat ha de ser −23,0 LUFS amb un marge de ±0,1 LU.
/// </summary>
public class LoudnessTests
{
    /// <summary>Sinus de 1000 Hz estèreo, els dos canals iguals, a <paramref name="dbfs"/> de pic.</summary>
    private static void AddSine(LoudnessMeter meter, int rate, double dbfs, double seconds)
    {
        float amplitude = (float)Math.Pow(10, dbfs / 20);
        var block = new float[rate / 10 * 2];
        long total = (long)(seconds * rate);
        for (long done = 0; done < total;)
        {
            int frames = (int)Math.Min(block.Length / 2, total - done);
            for (int f = 0; f < frames; f++)
            {
                float v = amplitude * (float)Math.Sin(2 * Math.PI * 1000 * (done + f) / rate);
                block[f * 2] = v;
                block[f * 2 + 1] = v;
            }
            meter.Add(block.AsSpan(0, frames * 2));
            done += frames;
        }
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void Cas_1_sinus_a_menys_23_dBFS_fa_menys_23_LUFS(int rate)
    {
        var meter = new LoudnessMeter(rate);
        AddSine(meter, rate, -23, 20);

        Assert.InRange(meter.IntegratedLufs!.Value, -23.1, -22.9);
    }

    [Fact]
    public void Cas_3_els_trams_fluixos_del_principi_i_del_final_no_compten()
    {
        // −36 dBFS 10 s, −23 dBFS 60 s, −36 dBFS 10 s: la porta relativa descarta els −36.
        var meter = new LoudnessMeter(48000);
        AddSine(meter, 48000, -36, 10);
        AddSine(meter, 48000, -23, 60);
        AddSine(meter, 48000, -36, 10);

        Assert.InRange(meter.IntegratedLufs!.Value, -23.1, -22.9);
    }

    [Fact]
    public void Cas_5_mitjana_de_trams_de_diferent_nivell()
    {
        // −26 dBFS 20 s, −20 dBFS 20,1 s, −26 dBFS 20 s: la mitjana d'energia dona −23.
        var meter = new LoudnessMeter(48000);
        AddSine(meter, 48000, -26, 20);
        AddSine(meter, 48000, -20, 20.1);
        AddSine(meter, 48000, -26, 20);

        Assert.InRange(meter.IntegratedLufs!.Value, -23.1, -22.9);
    }

    [Fact]
    public void Un_senyal_mes_fort_dona_un_valor_mes_alt_en_la_mateixa_proporcio()
    {
        var quiet = new LoudnessMeter(48000);
        var loud = new LoudnessMeter(48000);
        AddSine(quiet, 48000, -30, 5);
        AddSine(loud, 48000, -10, 5);

        Assert.InRange(loud.IntegratedLufs!.Value - quiet.IntegratedLufs!.Value, 19.95, 20.05);
    }

    [Fact]
    public void El_silenci_i_els_trossos_massa_curts_no_tenen_sonoritat()
    {
        var silence = new LoudnessMeter(48000);
        silence.Add(new float[48000 * 2 * 3]);
        var tooShort = new LoudnessMeter(48000);
        AddSine(tooShort, 48000, -10, 0.3);

        Assert.Null(silence.IntegratedLufs);
        Assert.Null(tooShort.IntegratedLufs);
    }

    [Fact]
    public void Guarda_el_pic_mes_alt()
    {
        var meter = new LoudnessMeter(48000);
        AddSine(meter, 48000, -6, 1);

        Assert.InRange(meter.SamplePeak, 0.500f, 0.502f);
    }

    [Fact]
    public void Un_senyal_mono_tambe_es_mesura()
    {
        // Mono = un sol canal: 3 dB menys que el mateix senyal als dos canals.
        var mono = new LoudnessMeter(48000, channels: 1);
        var buffer = new float[48000 * 5];
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = (float)(Math.Pow(10, -23.0 / 20) * Math.Sin(2 * Math.PI * 1000 * i / 48000));
        mono.Add(buffer);

        Assert.InRange(mono.IntegratedLufs!.Value, -26.1, -25.9);
    }
}
