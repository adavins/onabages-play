using OnaBages.Audio;

namespace OnaBages.Tests;

/// <summary>
/// Anivellador (NIV) dels enregistradors (fase 5, pas 32a; D-146). Es comprova al fitxer MP3: un tros fluix i un de fort
/// queden més igualats, i activar-lo o desactivar-lo a mig enregistrament no fa perdre ni repetir cap mostra.
/// </summary>
public class RecorderLevelerTests : IDisposable
{
    private const string DeviceId = "{0.0.1.00000000}.{proves}";
    private const int Rate = 48000;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "niv-" + Guid.NewGuid().ToString("N"));
    private FakeInput? _input;
    private int _frame;

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

    /// <summary>Envia <paramref name="seconds"/> d'un to de 440 Hz, continu amb l'anterior, en paquets de 10 ms.</summary>
    private void PushTone(double seconds, float amplitude)
    {
        int packet = Rate / 100;
        int total = (int)(Rate * seconds);
        var buffer = new float[packet * 2];
        for (int start = 0; start < total; start += packet, _frame += packet)
        {
            for (int i = 0; i < packet; i++)
                buffer[i * 2] = buffer[i * 2 + 1] = amplitude * (float)Math.Sin(2 * Math.PI * 440 * (_frame + i) / Rate);
            _input!.Push(buffer);
        }
    }

    private static float[] LeftChannel(string path)
    {
        using var source = AudioFileSource.Open(path, Rate);
        var all = TestAudio.ReadAll(source);
        return Enumerable.Range(0, all.Length / 2).Select(i => all[i * 2]).ToArray();
    }

    /// <summary>Nivell RMS (dB) entre dos instants del fitxer.</summary>
    private static double RmsDb(float[] samples, double from, double to)
    {
        int a = (int)(from * Rate), b = (int)(to * Rate);
        double sum = 0;
        for (int i = a; i < b; i++) sum += samples[i] * samples[i];
        return 10 * Math.Log10(sum / (b - a));
    }

    /// <summary>Enregistra 4 s fluixos i 4 s forts (20 dB més) i retorna la diferència de nivell al fitxer.</summary>
    private double Difference(bool leveler)
    {
        using var recorder = NewRecorder();
        recorder.SetLeveler(leveler, -12, 12);
        recorder.Start(_folder, leveler ? "Amb" : "Sense");
        PushTone(4, 0.05f);
        PushTone(4, 0.5f);
        recorder.Stop();
        var left = LeftChannel(recorder.FilePath!);
        return RmsDb(left, 6.5, 7.5) - RmsDb(left, 2.5, 3.5);
    }

    [Fact]
    public void Desactivat_per_defecte()
    {
        using var recorder = NewRecorder();
        Assert.False(recorder.LevelerEnabled);
        Assert.Null(recorder.LevelerGainDb);
    }

    [Fact]
    public void Iguala_un_tros_fluix_i_un_de_fort()
    {
        double without = Difference(leveler: false);
        double with = Difference(leveler: true);

        Assert.InRange(without, 19, 21);   // sense NIV, els 20 dB de diferència hi són
        Assert.InRange(with, -1, 9);       // amb NIV, molt més igualats (el fluix puja com a molt 12 dB)
    }

    [Fact]
    public void Activar_i_desactivar_a_mig_enregistrament_no_perd_ni_repeteix_res()
    {
        using var recorder = NewRecorder();
        recorder.Start(_folder, "Canvis");
        PushTone(2, 0.2f);
        recorder.SetLeveler(true, -12, 12);
        PushTone(2, 0.2f);
        Thread.Sleep(300);
        Assert.NotNull(recorder.LevelerGainDb); // treballa
        Assert.NotNull(OnaBages.Engine.Protocol.MetersJson.Event([], [recorder])["recorderLeveler"]!["1"]); // i va amb els mesuradors
        recorder.SetLeveler(false, -12, 12);
        PushTone(2, 0.2f);
        recorder.Stop();

        var analysis = Mp3Frames.Analyze(recorder.FilePath!);
        Assert.InRange(analysis.Duration.TotalSeconds, 5.98, 6.1);
        Assert.Equal(0, recorder.SamplesLost);
        Assert.Null(recorder.LevelerGainDb);
    }

    [Fact]
    public void Aturar_amb_el_NIV_activat_escriu_fins_al_final()
    {
        using var recorder = NewRecorder();
        recorder.SetLeveler(true, -12, 12);
        recorder.Start(_folder, "Final");
        PushTone(3, 0.2f);
        recorder.Stop(); // l'últim segon era dins l'anivellador: ha de sortir sencer

        Assert.Equal(RecorderState.Pending, recorder.State);
        var analysis = Mp3Frames.Analyze(recorder.FilePath!);
        Assert.InRange(analysis.Duration.TotalSeconds, 2.98, 3.1);
        Assert.False(File.Exists(recorder.FilePath + ".rec")); // tancat correctament
    }
    [Fact]
    public void Canviar_el_maxim_mentre_enregistra_fa_efecte_de_seguida()
    {
        using var recorder = NewRecorder();
        recorder.SetLeveler(true, -12, 12);
        recorder.Start(_folder, "Maxim");
        PushTone(4, 0.05f);           // fluix: vol pujar més de 12 dB, es queda a +12
        Thread.Sleep(500);            // que el fil de codificació hi arribi
        recorder.SetLeveler(true, -12, 3); // ⚙: el màxim baixa a +3 dB (sense desactivar el NIV)
        PushTone(4, 0.05f);
        recorder.Stop();

        var left = LeftChannel(recorder.FilePath!);
        double before = RmsDb(left, 2.0, 2.8), after = RmsDb(left, 6.5, 7.5);
        Assert.InRange(before - after, 7, 11); // de +12 a +3: uns 9 dB menys
    }

    [Fact]
    public void En_silenci_diu_que_esta_en_pausa_i_mante_el_guany()
    {
        using var recorder = NewRecorder();
        recorder.SetLeveler(true, -12, 12);
        recorder.Start(_folder, "Pausa");
        PushTone(3, 0.05f);
        Thread.Sleep(300);
        Assert.False(recorder.LevelerHolding);       // hi ha veu (el to)
        double gain = recorder.LevelerGainDb!.Value;

        var silence = new float[Rate / 100 * 2];
        for (int i = 0; i < 300; i++) _input!.Push(silence); // 3 s sense senyal
        Thread.Sleep(300);
        Assert.True(recorder.LevelerHolding);
        Assert.InRange(recorder.LevelerGainDb!.Value, gain - 0.5, gain + 0.5); // no puja ni torna a 0
        var leveler = OnaBages.Engine.Protocol.MetersJson.Event([], [recorder])["recorderLeveler"]!["1"]!;
        Assert.True(leveler["hold"]!.GetValue<bool>());
        recorder.Stop();
    }

    // --- Ordres ---

    [Fact]
    public void Ordres_perfil_i_ajustos()
    {
        var repository = new OnaBages.Engine.Data.ConfigRepository(Path.Combine(_folder, "dades", "config.json"));
        var store = new OnaBages.Engine.Data.ConfigStore(repository);
        store.Load();
        using var recorder = NewRecorder();
        var state = new OnaBages.Engine.EngineState([], [recorder]);
        var router = new OnaBages.Engine.Protocol.CommandRouter(state, () => { }, () => [], config: store);

        var r = router.Handle("""{ "v": 1, "id": 1, "cmd": "recorder.setLeveler", "recorder": "1", "enabled": true }""");
        Assert.True(r["result"]!["recorder"]!["leveler"]!["enabled"]!.GetValue<bool>());
        Assert.True(recorder.LevelerEnabled);
        Assert.Contains("1", repository.Load().Config.LeveledRecorders);
        Assert.Equal("bad_params", router.Handle("""{ "v": 1, "id": 2, "cmd": "recorder.setLeveler", "recorder": "1" }""")["error"]!["code"]!.GetValue<string>());

        // Canviar l'objectiu o el màxim a ⚙ arriba també als enregistradors i a l'emissor d'streaming.
        router.Handle("""{ "v": 1, "id": 3, "cmd": "settings.set", "loudness": { "targetLufs": -16 }, "leveler": { "maxBoostDb": 8 } }""");
        Assert.Equal((-16.0, 8.0), (recorder.LevelerTargetLufs, recorder.LevelerMaxBoostDb));
        Assert.True(recorder.LevelerEnabled);
        Assert.Equal((-16.0, 8.0), (state.Streamer.LevelerTargetLufs, state.Streamer.LevelerMaxBoostDb));
        Assert.False(state.Streamer.LevelerEnabled);

        router.Handle("""{ "v": 1, "id": 4, "cmd": "recorder.setLeveler", "recorder": "1", "enabled": false }""");
        Assert.False(recorder.LevelerEnabled);
        Assert.Empty(repository.Load().Config.LeveledRecorders);
    }
}
