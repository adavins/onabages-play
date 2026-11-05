using OnaBages.Audio;

namespace OnaBages.Tests;

/// <summary>
/// Compressor/limitador dels reproductors (fase 4, pas 4a; D-066). Es prova amb senyals sintètics:
/// el llindar, el sostre (que no es passa mai), els temps d'atac i d'alliberament, els canals
/// junts o separats, que no fa clics en canviar-lo i que no crea objectes al fil d'àudio.
/// </summary>
public class DynamicsTests
{
    private const int Rate = 48000;

    private static float Db(double db) => (float)Math.Pow(10, db / 20);

    private static double ToDb(double gain) => 20 * Math.Log10(gain);

    /// <summary>Sinus estèreo (els dos canals iguals) de <paramref name="seconds"/> segons.</summary>
    private static float[] Sine(double amplitudeDb, double seconds, double frequency = 1000)
    {
        int frames = (int)(seconds * Rate);
        var buffer = new float[frames * 2];
        float amplitude = Db(amplitudeDb);
        for (int f = 0; f < frames; f++)
        {
            float v = amplitude * (float)Math.Sin(2 * Math.PI * frequency * f / Rate);
            buffer[f * 2] = v;
            buffer[f * 2 + 1] = v;
        }
        return buffer;
    }

    /// <summary>Pic (dBFS) del tros final del bloc, en estat estable.</summary>
    private static double TailPeakDb(float[] buffer, double lastSeconds, int channel = -1)
    {
        int start = Math.Max(0, buffer.Length - (int)(lastSeconds * Rate) * 2);
        float peak = 0;
        for (int i = start; i < buffer.Length; i++)
        {
            if (channel >= 0 && i % 2 != channel) continue;
            peak = Math.Max(peak, Math.Abs(buffer[i]));
        }
        return ToDb(peak);
    }

    private static float MaxAbs(float[] buffer) => buffer.Max(Math.Abs);

    /// <summary>Processa en blocs de 10 ms, com faria la sortida d'àudio.</summary>
    private static void ProcessInBlocks(DynamicsProcessor dynamics, float[] buffer, int blockFrames = 480)
    {
        for (int i = 0; i < buffer.Length; i += blockFrames * 2)
            dynamics.Process(buffer.AsSpan(i, Math.Min(blockFrames * 2, buffer.Length - i)));
    }

    // --- Desactivat ---

    [Fact]
    public void Desactivat_el_so_passa_exactament_igual()
    {
        var input = Sine(+6, 0.2); // també un senyal que passa de 0 dBFS: desactivat, no es toca
        var output = (float[])input.Clone();
        var dynamics = new DynamicsProcessor(Rate);

        ProcessInBlocks(dynamics, output);

        Assert.Equal(input, output);
    }

    [Fact]
    public void Desactivar_lo_a_mig_so_no_fa_clics_i_despres_passa_igual()
    {
        var input = Sine(-12, 1, frequency: 100);
        var buffer = (float[])input.Clone();
        var dynamics = new DynamicsProcessor(Rate, DynamicsSettings.SqrSoftLaCentral);
        dynamics.Process(buffer.AsSpan(0, Rate));
        dynamics.Settings = new DynamicsSettings();
        dynamics.Process(buffer.AsSpan(Rate));

        float maxStep = 0;
        for (int i = 2; i < buffer.Length; i += 2)
            maxStep = Math.Max(maxStep, Math.Abs(buffer[i] - buffer[i - 2]));
        Assert.True(maxStep < 0.02f, $"salt de {maxStep}");
        // Passada la transició, exactament el so d'entrada.
        Assert.Equal(input.AsSpan(input.Length - 960).ToArray(), buffer.AsSpan(buffer.Length - 960).ToArray());
    }

    // --- Configuració del SqrSoft (La Central) ---

    [Fact]
    public void Per_sota_del_llindar_nomes_sapuja_el_guany()
    {
        // −20 dBFS + 9,5 dB = −10,5 dBFS: per sota de −2,9, no comprimeix.
        var buffer = Sine(-20, 1);
        ProcessInBlocks(new DynamicsProcessor(Rate, DynamicsSettings.SqrSoftLaCentral), buffer);

        Assert.InRange(TailPeakDb(buffer, 0.3), -10.6, -10.4);
    }

    [Fact]
    public void Per_sobre_del_llindar_es_queda_a_prop_del_llindar()
    {
        // −3 dBFS + 9,5 dB = +6,5 dBFS: amb ràtio 100 s'ha de quedar a prop de −2,9 dBFS.
        var buffer = Sine(-3, 2);
        ProcessInBlocks(new DynamicsProcessor(Rate, DynamicsSettings.SqrSoftLaCentral), buffer);

        Assert.InRange(TailPeakDb(buffer, 0.5), -3.4, -2.3);
    }

    [Fact]
    public void Una_pista_fluixa_i_una_de_forta_acaben_sonant_semblant()
    {
        var quiet = Sine(-12, 2);
        var loud = Sine(0, 2);
        ProcessInBlocks(new DynamicsProcessor(Rate, DynamicsSettings.SqrSoftLaCentral), quiet);
        ProcessInBlocks(new DynamicsProcessor(Rate, DynamicsSettings.SqrSoftLaCentral), loud);

        // Abans: 12 dB de diferència. Després: menys de 1 dB.
        Assert.True(Math.Abs(TailPeakDb(loud, 0.5) - TailPeakDb(quiet, 0.5)) < 1);
    }

    // --- Sostre: no es passa mai ---

    [Theory]
    [InlineData(4.2, 1450, 100)]   // SqrSoft
    [InlineData(200, 5000, 1)]     // sense compressió: només el sostre
    [InlineData(0.1, 10, 100)]     // tan ràpid com es pot
    public void El_sostre_no_es_passa_mai_ni_amb_senyals_extrems(double attackMs, double releaseMs, double ratio)
    {
        var settings = new DynamicsSettings(true, InputGainDb: 30, ThresholdDb: -2.9, Ratio: ratio,
            AttackMs: attackMs, ReleaseMs: releaseMs, CeilingDb: -0.3);
        var dynamics = new DynamicsProcessor(Rate, settings);
        var random = new Random(1234);
        var buffer = new float[Rate * 2 * 2]; // 2 s
        for (int i = 0; i < buffer.Length; i++)
        {
            // Soroll fort amb cops aïllats (transitoris) de tant en tant.
            buffer[i] = (float)(random.NextDouble() * 2 - 1) * (i % 9600 < 4 ? 4f : 0.5f);
        }

        ProcessInBlocks(dynamics, buffer, blockFrames: 441);

        Assert.True(MaxAbs(buffer) <= Db(-0.3) + 1e-6f, $"pic {ToDb(MaxAbs(buffer)):F3} dBFS");
    }

    // --- Atac i alliberament ---

    /// <summary>Abaixada (dB) mostra a mostra mentre sona un senyal quadrat (nivell constant).</summary>
    private static List<float> ReductionTrace(DynamicsSettings settings, double levelDb, double seconds, DynamicsProcessor? reuse = null)
    {
        var dynamics = reuse ?? new DynamicsProcessor(Rate, settings);
        var trace = new List<float>();
        float level = Db(levelDb);
        var frame = new float[2];
        for (int f = 0; f < (int)(seconds * Rate); f++)
        {
            float v = (f / 240) % 2 == 0 ? level : -level; // quadrat de 100 Hz
            frame[0] = v; frame[1] = v;
            dynamics.Process(frame);
            trace.Add(dynamics.TakeMaxReductionDb());
        }
        return trace;
    }

    [Fact]
    public void Latac_fa_el_63_per_cent_de_labaixada_en_el_temps_datac()
    {
        // −10 dBFS amb llindar −20 i ràtio 100: s'ha d'abaixar 9,9 dB. Sostre a 0: no hi intervé.
        var settings = new DynamicsSettings(true, ThresholdDb: -20, Ratio: 100, AttackMs: 10, ReleaseMs: 1000, CeilingDb: 0);

        var trace = ReductionTrace(settings, -10, 0.2);

        Assert.InRange(trace[(int)(0.010 * Rate)], 0.632 * 9.9 - 0.4, 0.632 * 9.9 + 0.4);
        Assert.InRange(trace[^1], 9.8, 10.0);
    }

    [Fact]
    public void Lalliberament_torna_a_pujar_a_poc_a_poc()
    {
        var settings = new DynamicsSettings(true, ThresholdDb: -20, Ratio: 100, AttackMs: 1, ReleaseMs: 500, CeilingDb: 0);
        var dynamics = new DynamicsProcessor(Rate, settings);
        ReductionTrace(settings, -10, 0.2, dynamics); // abaixada de 9,9 dB

        var trace = ReductionTrace(settings, -60, 1, dynamics); // silenci (per sota del llindar)

        // En 500 ms queda un 37 % (3,6 dB); en 1 s, un 13 % (1,3 dB).
        Assert.InRange(trace[(int)(0.5 * Rate)], 0.368 * 9.9 - 0.4, 0.368 * 9.9 + 0.4);
        Assert.InRange(trace[^1], 0.135 * 9.9 - 0.3, 0.135 * 9.9 + 0.3);
    }

    // --- Canals junts o separats ---

    [Fact]
    public void Amb_els_canals_separats_un_cop_a_lesquerra_no_abaixa_la_dreta()
    {
        float[] Run(bool link)
        {
            var settings = new DynamicsSettings(true, ThresholdDb: -20, Ratio: 100, AttackMs: 1, ReleaseMs: 100, StereoLink: link, CeilingDb: 0);
            var buffer = Sine(-6, 1);
            for (int i = 1; i < buffer.Length; i += 2) buffer[i] *= Db(-30); // dreta: −36 dBFS
            ProcessInBlocks(new DynamicsProcessor(Rate, settings), buffer);
            return buffer;
        }

        var separate = Run(link: false);
        var linked = Run(link: true);

        Assert.InRange(TailPeakDb(separate, 0.3, channel: 1), -36.2, -35.8); // la dreta, intacta
        Assert.True(TailPeakDb(linked, 0.3, channel: 1) < -45);              // la dreta baixa amb l'esquerra
    }

    // --- Sense clics ni objectes nous ---

    [Fact]
    public void Activar_lo_a_mig_so_no_fa_clics()
    {
        var buffer = Sine(-12, 1, frequency: 100);
        var dynamics = new DynamicsProcessor(Rate);
        dynamics.Process(buffer.AsSpan(0, Rate)); // 0,5 s desactivat
        dynamics.Settings = DynamicsSettings.SqrSoftLaCentral;
        dynamics.Process(buffer.AsSpan(Rate));

        // El salt més gran entre dues mostres seguides: un sinus de 100 Hz no en té cap de gran.
        // Si el guany d'entrada (+9,5 dB) arribés de cop, hi hauria un salt de més de 0,1.
        float maxStep = 0;
        for (int i = 2; i < buffer.Length; i += 2)
            maxStep = Math.Max(maxStep, Math.Abs(buffer[i] - buffer[i - 2]));
        Assert.True(maxStep < 0.02f, $"salt de {maxStep}");
    }

    [Fact]
    public void Processar_no_crea_objectes()
    {
        var dynamics = new DynamicsProcessor(Rate, DynamicsSettings.SqrSoftLaCentral);
        var buffer = Sine(-3, 1);
        dynamics.Process(buffer.AsSpan(0, 960)); // escalfar

        long before = GC.GetAllocatedBytesForCurrentThread();
        ProcessInBlocks(dynamics, buffer);
        dynamics.TakeMaxReductionDb();
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }

    [Fact]
    public void Lindicador_diu_quant_sabaixa()
    {
        var dynamics = new DynamicsProcessor(Rate, DynamicsSettings.SqrSoftLaCentral);
        ProcessInBlocks(dynamics, Sine(-3, 1));
        dynamics.TakeMaxReductionDb(); // el començament (abans que actuï el compressor, el sostre abaixa molt)
        ProcessInBlocks(dynamics, Sine(-3, 1));

        // Estable: +6,5 dBFS d'entrada fins a uns −2,8: unes 9,3 dB d'abaixada.
        Assert.InRange(dynamics.TakeMaxReductionDb(), 8.5, 10.5);
        Assert.Equal(0, dynamics.TakeMaxReductionDb()); // es posa a zero en llegir-la
    }

    [Fact]
    public void Els_valors_fora_de_marge_es_porten_al_limit()
    {
        var s = new DynamicsSettings(true, InputGainDb: 99, ThresholdDb: 5, Ratio: 0, AttackMs: double.NaN, ReleaseMs: 1e9, CeilingDb: 3).Clamped();

        Assert.Equal(DynamicsSettings.MaxGainDb, s.InputGainDb);
        Assert.Equal(0, s.ThresholdDb);
        Assert.Equal(1, s.Ratio);
        Assert.Equal(DynamicsSettings.MinAttackMs, s.AttackMs);
        Assert.Equal(DynamicsSettings.MaxReleaseMs, s.ReleaseMs);
        Assert.Equal(0, s.CeilingDb);
    }
}
