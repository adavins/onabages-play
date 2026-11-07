using OnaBages.Audio;

namespace OnaBages.Tests;

/// <summary>
/// Anivellador (fase 5, pas 30a; D-134): una «entrevista» amb una veu fluixa i una de forta surt amb les dues a prop de
/// l'objectiu, sense pujar les pauses, sense salts, sense saturar i sense perdre cap mostra.
/// </summary>
public class LevelerTests
{
    private const int Rate = 48000;
    private const double Target = -16;

    /// <summary>
    /// «Parla» sintètica: trossos de 400 ms de to (la freqüència canvia d'un tros a l'altre) amb 200 ms de silenci,
    /// a <paramref name="peakDb"/> de pic. <paramref name="right"/> false: el canal dret és buit.
    /// </summary>
    private static void Speech(List<float> into, double seconds, double peakDb, bool right = true)
    {
        float amplitude = (float)Math.Pow(10, peakDb / 20);
        int frames = (int)(seconds * Rate);
        int start = into.Count / 2;
        for (int i = 0; i < frames; i++)
        {
            int t = start + i;
            int piece = t / (Rate * 6 / 10);            // trossos de 600 ms
            bool sound = t % (Rate * 6 / 10) < Rate * 4 / 10;
            double hz = 300 + 150 * (piece % 5);
            float s = sound ? amplitude * (float)Math.Sin(2 * Math.PI * hz * t / Rate) : 0f;
            into.Add(s);
            into.Add(right ? s : 0f);
        }
    }

    private static void Noise(List<float> into, double seconds, double db)
    {
        var random = new Random(1);
        float amplitude = (float)Math.Pow(10, db / 20);
        for (int i = 0; i < (int)(seconds * Rate); i++)
        {
            float s = amplitude * (float)(random.NextDouble() * 2 - 1);
            into.Add(s);
            into.Add(s);
        }
    }

    /// <summary>Passa tot el senyal per l'anivellador, de 100 ms en 100 ms, i apunta el guany de cada tros.</summary>
    private static (float[] Output, List<double> Gains) Run(float[] input, double target = Target, double maxBoost = 12, bool right = true)
    {
        int pos = 0;
        var leveler = new Leveler(span =>
        {
            int n = Math.Min(span.Length, input.Length - pos);
            input.AsSpan(pos, n).CopyTo(span);
            pos += n;
            return n;
        }, Rate, target, maxBoost);

        var output = new List<float>();
        var gains = new List<double>();
        var buffer = new float[Rate / 10 * 2];
        int read;
        while ((read = leveler.Read(buffer)) > 0)
        {
            output.AddRange(buffer.AsSpan(0, read).ToArray());
            gains.Add(leveler.CurrentGainDb);
        }
        return ([.. output], gains);
    }

    /// <summary>Sonoritat (LUFS) d'un tros, de <paramref name="from"/> a <paramref name="to"/> segons.</summary>
    private static double Lufs(float[] samples, double from, double to)
    {
        var meter = new LoudnessMeter(Rate);
        meter.Add(samples.AsSpan((int)(from * Rate) * 2, (int)((to - from) * Rate) * 2));
        return meter.IntegratedLufs!.Value;
    }

    [Fact]
    public void Una_veu_fluixa_i_una_forta_queden_a_prop_de_l_objectiu()
    {
        var input = new List<float>();
        Speech(input, 8, -26);   // entrevistador, fluix
        Speech(input, 8, -8);    // entrevistat, fort
        Speech(input, 8, -26);
        var samples = input.ToArray();
        double before = Lufs(samples, 10, 16) - Lufs(samples, 2, 8);
        Assert.InRange(before, 16, 20); // abans: ~18 dB de diferència

        var (output, _) = Run(samples);

        Assert.Equal(samples.Length, output.Length); // no es perd ni s'afegeix cap mostra
        // Lluny dels canvis (2 s), cada veu a ±2 dB de l'objectiu.
        Assert.InRange(Lufs(output, 2, 7), Target - 2, Target + 2);
        Assert.InRange(Lufs(output, 10, 15), Target - 2, Target + 2);
        Assert.InRange(Lufs(output, 18, 23), Target - 2, Target + 2);
    }

    [Fact]
    public void Va_a_poc_a_poc_sense_salts()
    {
        var input = new List<float>();
        Speech(input, 6, -26);
        Speech(input, 6, -8);
        Speech(input, 6, -26);
        var (_, gains) = Run(input.ToArray());

        for (int i = 1; i < gains.Count; i++)
        {
            double step = gains[i] - gains[i - 1];
            Assert.InRange(step, -Leveler.DownDbPerSecond / 10 - 0.01, Leveler.UpDbPerSecond / 10 + 0.01);
        }
        // El canvi a la veu forta comença abans que arribi (mira 1 s endavant): just abans ja ha baixat.
        Assert.True(gains[59] < gains[45] - 3, $"a 5,9 s: {gains[59]:F1} dB; a 4,5 s: {gains[45]:F1} dB");
    }

    [Fact]
    public void A_les_pauses_no_puja()
    {
        var input = new List<float>();
        Speech(input, 5, -12);
        Noise(input, 5, -68);    // pausa llarga amb soroll de fons
        Speech(input, 5, -12);
        var (output, gains) = Run(input.ToArray());

        double beforePause = gains[48];
        // A la cua de l'última frase (encara compta com a veu) com a molt mig dB; dins de la pausa, gens.
        for (int i = 50; i < 98; i++) Assert.True(gains[i] <= beforePause + 0.5, $"a {i / 10.0:F1} s: {gains[i]:F1} dB (abans {beforePause:F1})");
        for (int i = 55; i < 98; i++) Assert.True(gains[i] <= gains[54] + 0.01, $"a {i / 10.0:F1} s: {gains[i]:F1} dB (a 5,4 s {gains[54]:F1})");
        // El soroll de la pausa no s'ha pujat: continua molt per sota.
        float noisePeak = output.AsSpan(6 * Rate * 2, 3 * Rate * 2).ToArray().Max(Math.Abs);
        Assert.True(20 * Math.Log10(noisePeak) < -60);
    }

    [Fact]
    public void No_satura()
    {
        var input = new List<float>();
        Speech(input, 4, -30);
        Speech(input, 0.5, -0.5);   // un cop fort de sobte, després d'una veu molt fluixa (guany alt)
        Speech(input, 4, -30);
        var (output, _) = Run(input.ToArray(), target: -10, maxBoost: 20);
        float peak = output.Max(Math.Abs);
        Assert.True(20 * Math.Log10(peak) <= Leveler.CeilingDb + 0.2, $"pic {20 * Math.Log10(peak):F2} dBFS");
    }

    [Fact]
    public void Com_a_molt_puja_el_maxim()
    {
        var input = new List<float>();
        Speech(input, 6, -40);
        var (output, gains) = Run(input.ToArray(), maxBoost: 12);
        Assert.InRange(gains.Max(), 11.9, 12.01);
        Assert.InRange(Lufs(output, 1, 5) - Lufs(input.ToArray(), 1, 5), 11.5, 12.5);
    }

    [Fact]
    public void Si_ja_hi_es_no_ho_toca()
    {
        var input = new List<float>();
        Speech(input, 6, -10);
        var samples = input.ToArray();
        double level = Lufs(samples, 0, 6);
        var (output, gains) = Run(samples, target: level);
        // Mesura com la normalització (blocs de 400 ms, porta relativa), però en finestres de ±1 s: gairebé no la toca.
        Assert.All(gains, g => Assert.InRange(g, -0.6, 0.6));
        Assert.InRange(Lufs(output, 0, 6), level - 0.5, level + 0.5);
    }

    [Fact]
    public void Una_pista_amb_un_canal_buit_es_mesura_com_si_sonessin_els_dos()
    {
        var both = new List<float>();
        Speech(both, 6, -20);
        var one = new List<float>();
        Speech(one, 6, -20, right: false);
        var (_, gainsBoth) = Run(both.ToArray());
        var (_, gainsOne) = Run(one.ToArray());
        Assert.Equal(gainsBoth[30], gainsOne[30], 0.1);
    }

    [Fact]
    public void Una_pista_curta_o_buida()
    {
        Assert.Empty(Run([]).Output);
        var input = new List<float>();
        Speech(input, 0.25, -20);
        Assert.Equal(input.Count, Run(input.ToArray()).Output.Length);
    }
}
