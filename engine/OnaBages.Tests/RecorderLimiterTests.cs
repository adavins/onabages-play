using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>
/// Limitador opcional dels enregistradors (fase 5, pas 9a; D-083). Es comprova al fitxer MP3 descodificat:
/// el que passaria de 0 dB queda sota el sostre, el so fluix no es toca i no es perd cap tros del final.
/// </summary>
public class RecorderLimiterTests : IDisposable
{
    private const string DeviceId = "{0.0.1.00000000}.{proves}";
    private const int Rate = 48000;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "lim-" + Guid.NewGuid().ToString("N"));
    private FakeInput? _input;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private Recorder NewRecorder()
    {
        var recorder = new Recorder("1", _ => _input = new FakeInput(Rate, 2));
        recorder.SetDevice(DeviceId);
        return recorder;
    }

    /// <summary>Envia <paramref name="seconds"/> d'un to de 440 Hz d'amplitud <paramref name="amplitude"/>, en paquets de 10 ms.</summary>
    private void PushTone(double seconds, float amplitude, int startFrame = 0)
    {
        int packet = Rate / 100;
        int total = (int)(Rate * seconds);
        var buffer = new float[packet * 2];
        for (int start = 0; start < total; start += packet)
        {
            for (int i = 0; i < packet; i++)
                buffer[i * 2] = buffer[i * 2 + 1] = amplitude * (float)Math.Sin(2 * Math.PI * 440 * (startFrame + start + i) / Rate);
            _input!.Push(buffer);
        }
    }

    /// <summary>Enregistra un to i retorna el canal esquerre del fitxer descodificat.</summary>
    private float[] Record(bool limiter, float amplitude, double gainDb = 0, double ceilingDb = -1, double seconds = 1)
    {
        using var recorder = NewRecorder();
        recorder.SetGain(gainDb);
        recorder.SetLimiter(limiter, ceilingDb);
        recorder.Start(_folder, "Limitador");
        PushTone(seconds, amplitude);
        recorder.Stop();
        using var source = AudioFileSource.Open(recorder.FilePath!, Rate);
        var all = TestAudio.ReadAll(source);
        return Enumerable.Range(0, all.Length / 2).Select(i => all[i * 2]).ToArray();
    }

    private static double PeakDb(float[] samples) => 20 * Math.Log10(samples.Max(Math.Abs));

    [Fact]
    public void Desactivat_per_defecte_i_el_so_passa_igual()
    {
        using var recorder = NewRecorder();
        Assert.False(recorder.LimiterEnabled);
        Assert.Equal(-1.0, recorder.LimiterCeilingDb);

        var output = Record(limiter: false, amplitude: 0.97f);  // −0,26 dBFS

        Assert.InRange(PeakDb(output), -0.6, 0.3);
    }

    [Fact]
    public void Activat_el_que_passaria_de_0_dB_queda_sota_el_sostre()
    {
        // To a 0,6 amb +6 dB de volum: 1,2 (passaria de 0 dBFS). Sense limitador es retallaria.
        var output = Record(limiter: true, amplitude: 0.6f, gainDb: 6, ceilingDb: -1);

        // Sostre −1 dB; l'MP3 descodificat pot passar uns pocs dècims de dB.
        Assert.InRange(PeakDb(output), -1.6, -0.6);
    }

    [Fact]
    public void Amb_sostre_de_menys_0_1_tambe()
    {
        var output = Record(limiter: true, amplitude: 0.6f, gainDb: 6, ceilingDb: -0.1);

        Assert.InRange(PeakDb(output), -0.7, 0.3);
    }

    [Fact]
    public void Per_sota_del_sostre_no_toca_res()
    {
        var on = Record(limiter: true, amplitude: 0.3f);
        var off = Record(limiter: false, amplitude: 0.3f);

        Assert.Equal(PeakDb(off), PeakDb(on), 0.05);
    }

    [Fact]
    public void No_es_perd_el_final_de_l_enregistrament()
    {
        // El limitador retarda el so 1,5 ms (mira endavant). En aturar, aquest tros ha de sortir igualment:
        // l'últim so arriba 71 mostres (1,5 ms a 48 kHz) més tard que sense limitador, no abans.
        var on = Record(limiter: true, amplitude: 0.3f);
        var off = Record(limiter: false, amplitude: 0.3f);

        int lastOn = Array.FindLastIndex(on, v => Math.Abs(v) > 0.05f);
        int lastOff = Array.FindLastIndex(off, v => Math.Abs(v) > 0.05f);
        Assert.InRange(lastOn - lastOff, 65, 77);
    }

    [Fact]
    public void Es_pot_activar_a_mig_enregistrament_i_indica_quant_abaixa()
    {
        using var recorder = NewRecorder();
        recorder.SetGain(6);
        recorder.Start(_folder, "Mig");
        PushTone(0.5, 0.6f);
        Thread.Sleep(300);
        Assert.Equal(0f, recorder.TakeLimiterReductionDb()); // desactivat: no abaixa

        recorder.SetLimiter(true, -1);
        PushTone(1, 0.6f, startFrame: Rate / 2);
        Thread.Sleep(300);
        float reduction = recorder.TakeLimiterReductionDb();
        recorder.Stop();

        Assert.InRange(reduction, 2.0f, 3.5f);                   // 1,2 → −1 dB: uns 2,6 dB
        Assert.Equal(0f, recorder.TakeLimiterReductionDb());     // aturat: res
        Assert.Equal(0, recorder.SamplesLost);
    }

    [Fact]
    public void El_mesurador_mostra_el_nivell_despres_del_limitador()
    {
        // Error trobat pel desenvolupador: amb el limitador, el mesurador arribava a 0 dB i s'encenia CLIP.
        using var recorder = NewRecorder();
        recorder.SetGain(6);
        PushTone(0.1, 0.6f);                                       // 1,2: passaria de 0 dB (sense enregistrar)
        Assert.True(PeakMeter.ToDb(recorder.Meter.Take().Left) > 0);

        recorder.SetLimiter(true, -1);
        PushTone(0.1, 0.6f);
        Assert.Equal(-1.0, PeakMeter.ToDb(recorder.Meter.Take().Left), 0.05);

        PushTone(0.1, 0.1f);                                       // per sota del sostre, el nivell de debò
        Assert.Equal(20 * Math.Log10(0.2), PeakMeter.ToDb(recorder.Meter.Take().Left), 0.1);
    }

    // --- Ordres ---

    [Fact]
    public void Ordres_i_perfil()
    {
        var repository = new ConfigRepository(Path.Combine(_folder, "dades", "config.json"));
        var store = new ConfigStore(repository);
        store.Load();
        using var recorder = NewRecorder();
        var router = new CommandRouter(new EngineState([], [recorder]), () => { }, () => [], config: store);

        var r = router.Handle("""{ "v": 1, "id": 1, "cmd": "recorder.setLimiter", "recorder": "1", "enabled": true }""");
        var limiter = r["result"]!["recorder"]!["limiter"]!;
        Assert.True(limiter["enabled"]!.GetValue<bool>());
        Assert.Equal(-1.0, limiter["ceilingDb"]!.GetValue<double>());
        Assert.Contains("1", repository.Load().Config.LimitedRecorders);

        r = router.Handle("""{ "v": 1, "id": 2, "cmd": "settings.set", "recorderLimiter": { "ceilingDb": -0.1 } }""");
        Assert.Equal(-0.1, r["result"]!["settings"]!["recorderLimiter"]!["ceilingDb"]!.GetValue<double>());
        Assert.Equal(-0.1, recorder.LimiterCeilingDb);
        Assert.True(recorder.LimiterEnabled);
        Assert.Equal(-0.1, repository.Load().Config.RecorderLimiterCeilingDb);

        Assert.Equal("bad_params", router.Handle("""{ "v": 1, "id": 3, "cmd": "settings.set", "recorderLimiter": { "ceilingDb": 1 } }""")["error"]!["code"]!.GetValue<string>());
        Assert.Equal("bad_params", router.Handle("""{ "v": 1, "id": 4, "cmd": "recorder.setLimiter", "recorder": "1" }""")["error"]!["code"]!.GetValue<string>());

        router.Handle("""{ "v": 1, "id": 5, "cmd": "recorder.setLimiter", "recorder": "1", "enabled": false }""");
        Assert.False(recorder.LimiterEnabled);
        Assert.Empty(repository.Load().Config.LimitedRecorders);

        var meters = MetersJson.Event([], [recorder]);
        Assert.Equal(0, meters["recorderReduction"]!["1"]!.GetValue<double>());
    }
}
