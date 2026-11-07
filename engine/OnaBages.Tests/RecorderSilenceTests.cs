using System.Diagnostics;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>
/// Alarma de silenci dels enregistradors (fase 5, pas 25a; D-121): salta quan el que entra al fitxer fa una estona
/// que és per sota del llindar, s'apaga quan torna el so o en aturar, i els ajustos són del perfil.
/// </summary>
public class RecorderSilenceTests : IDisposable
{
    private const string DeviceId = "{0.0.1.00000000}.{proves}";
    private const int Rate = 48000;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "sil-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Envia un to de 440 Hz (amplitud 0 = silenci), en paquets de 10 ms.</summary>
    private void Push(double seconds, float amplitude)
    {
        int packet = Rate / 100;
        var buffer = new float[packet * 2];
        for (int start = 0; start < (int)(Rate * seconds); start += packet)
        {
            for (int i = 0; i < packet; i++)
                buffer[i * 2] = buffer[i * 2 + 1] = amplitude * (float)Math.Sin(2 * Math.PI * 440 * (start + i) / Rate);
            _input!.Push(buffer);
        }
    }

    /// <summary>Espera que el fil de codificació hagi passat tot el que s'ha enviat (o fins a 10 s).</summary>
    private static void WaitFor(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(20);
    }

    [Fact]
    public void Per_defecte_activada_a_menys_50_dB_i_30_s()
    {
        using var recorder = NewRecorder();
        Assert.True(recorder.SilenceAlarmEnabled);
        Assert.Equal(-50, recorder.SilenceThresholdDb);
        Assert.Equal(30, recorder.SilenceSeconds);
        Assert.False(recorder.SilenceAlarm);
    }

    [Fact]
    public void Salta_amb_silenci_i_s_apaga_quan_torna_el_so()
    {
        using var recorder = NewRecorder();
        recorder.SetSilenceAlarm(true, -50, 5);
        recorder.Start(_folder, "Silenci");
        int changes = 0;
        recorder.StateChanged += _ => changes++;

        Push(4, 0f);
        WaitFor(() => recorder.SilentSeconds >= 3.9);
        recorder.Tick();
        Assert.False(recorder.SilenceAlarm);                    // encara no fa prou estona

        Push(1.5, 0f);
        WaitFor(() => recorder.SilentSeconds >= 5.4);
        recorder.Tick();
        Assert.True(recorder.SilenceAlarm);
        Assert.Equal(1, changes);                               // la interfície se n'assabenta
        var json = RecorderJson.Recorder(recorder)["silence"]!;
        Assert.True(json["alarm"]!.GetValue<bool>());
        Assert.InRange(json["seconds"]!.GetValue<double>(), 5.4, 5.6);

        Push(0.2, 0.3f);
        WaitFor(() => recorder.SilentSeconds == 0);
        recorder.Tick();
        Assert.False(recorder.SilenceAlarm);
        recorder.Stop();
    }

    [Fact]
    public void El_so_per_sota_del_llindar_compta_com_a_silenci()
    {
        using var recorder = NewRecorder();
        recorder.SetSilenceAlarm(true, -50, 5);
        recorder.Start(_folder, "Fluix");

        Push(5.5, 0.001f);                                      // −60 dBFS: soroll de fons
        WaitFor(() => recorder.SilentSeconds >= 5.4);
        recorder.Tick();
        Assert.True(recorder.SilenceAlarm);

        recorder.SetSilenceAlarm(true, -70, 5);                 // amb un llindar més baix, ja és so
        Push(0.2, 0.001f);
        WaitFor(() => recorder.SilentSeconds == 0);
        recorder.Tick();
        Assert.False(recorder.SilenceAlarm);
        recorder.Stop();
    }

    [Fact]
    public void Desactivada_no_salta_i_en_aturar_s_apaga()
    {
        using var recorder = NewRecorder();
        recorder.SetSilenceAlarm(false, -50, 5);
        recorder.Start(_folder, "Desactivada");
        Push(5.5, 0f);
        WaitFor(() => recorder.SilentSeconds >= 5.4);
        recorder.Tick();
        Assert.False(recorder.SilenceAlarm);

        recorder.SetSilenceAlarm(true, -50, 5);                 // activar-la a mig enregistrament: salta de seguida
        Assert.True(recorder.SilenceAlarm);

        recorder.Stop();
        Assert.False(recorder.SilenceAlarm);
        Assert.Equal(0, recorder.SilentSeconds);
    }

    [Fact]
    public void Sense_enregistrar_no_hi_ha_alarma()
    {
        using var recorder = NewRecorder();
        recorder.SetSilenceAlarm(true, -50, 5);
        Push(6, 0f);                                            // només el mesurador
        recorder.Tick();
        Assert.False(recorder.SilenceAlarm);
        Assert.Equal(0, recorder.SilentSeconds);
    }

    [Fact]
    public void Valors_fora_de_marge_es_porten_al_limit()
    {
        using var recorder = NewRecorder();
        recorder.SetSilenceAlarm(true, -100, 1);
        Assert.Equal(Recorder.MinSilenceThresholdDb, recorder.SilenceThresholdDb);
        Assert.Equal(Recorder.MinSilenceSeconds, recorder.SilenceSeconds);
        recorder.SetSilenceAlarm(true, 0, 10_000);
        Assert.Equal(Recorder.MaxSilenceThresholdDb, recorder.SilenceThresholdDb);
        Assert.Equal(Recorder.MaxSilenceSeconds, recorder.SilenceSeconds);
    }

    // --- Ordres ---

    [Fact]
    public void Ajustos_al_perfil_i_a_tots_els_enregistradors()
    {
        var repository = new ConfigRepository(Path.Combine(_folder, "dades", "config.json"));
        var store = new ConfigStore(repository);
        store.Load();
        using var recorder = NewRecorder();
        var router = new CommandRouter(new EngineState([], [recorder]), () => { }, () => [], config: store);

        var r = router.Handle("""{ "v": 1, "id": 1, "cmd": "settings.get" }""");
        var silence = r["result"]!["settings"]!["silenceAlarm"]!;
        Assert.True(silence["enabled"]!.GetValue<bool>());
        Assert.Equal(-50, silence["thresholdDb"]!.GetValue<double>());
        Assert.Equal(30, silence["seconds"]!.GetValue<int>());

        r = router.Handle("""{ "v": 1, "id": 2, "cmd": "settings.set", "silenceAlarm": { "enabled": false, "thresholdDb": -60, "seconds": 45 } }""");
        silence = r["result"]!["settings"]!["silenceAlarm"]!;
        Assert.False(silence["enabled"]!.GetValue<bool>());
        Assert.False(recorder.SilenceAlarmEnabled);
        Assert.Equal(-60, recorder.SilenceThresholdDb);
        Assert.Equal(45, recorder.SilenceSeconds);
        var saved = repository.Load().Config;
        Assert.False(saved.SilenceAlarmEnabled);
        Assert.Equal(-60, saved.SilenceThresholdDb);
        Assert.Equal(45, saved.SilenceSeconds);

        // Només un camp: els altres no canvien.
        router.Handle("""{ "v": 1, "id": 3, "cmd": "settings.set", "silenceAlarm": { "enabled": true } }""");
        Assert.True(recorder.SilenceAlarmEnabled);
        Assert.Equal(45, recorder.SilenceSeconds);

        Assert.Equal("bad_params", router.Handle("""{ "v": 1, "id": 4, "cmd": "settings.set", "silenceAlarm": { "seconds": 2 } }""")["error"]!["code"]!.GetValue<string>());
        Assert.Equal("bad_params", router.Handle("""{ "v": 1, "id": 5, "cmd": "settings.set", "silenceAlarm": { "thresholdDb": -20 } }""")["error"]!["code"]!.GetValue<string>());
        Assert.Equal("bad_params", router.Handle("""{ "v": 1, "id": 6, "cmd": "settings.set", "silenceAlarm": { "enabled": "sí" } }""")["error"]!["code"]!.GetValue<string>());
        Assert.Equal(45, repository.Load().Config.SilenceSeconds);
    }
}
