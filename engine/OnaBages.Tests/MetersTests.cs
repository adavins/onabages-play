using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>Volum dels reproductors i mesuradors de nivell (pas 3 de la fase 2).</summary>
public class MetersTests
{
    private const string OutputId = "{0.0.0.00000000}.{proves}";
    private const string InputId = "{0.0.1.00000000}.{proves}";

    private FakeOutput? _output;
    private FakeInput? _input;

    /// <summary>Reproductor amb un WAV d'1 s (canal esquerre a 0,1 i dret a 0,2 de pic), sonant.</summary>
    private Player PlayingPlayer()
    {
        var player = new Player("1", _ => _output = new FakeOutput());
        player.SetDevice(OutputId);
        player.Load(TestAudio.CreateWav(48000, 2, 1.0));
        player.Play();
        Thread.Sleep(100); // que el fil de lectura ompli la memòria
        return player;
    }

    private static float MaxAbs(float[] samples, int channel) =>
        samples.Where((_, i) => i % 2 == channel).Max(Math.Abs);

    // --- PeakMeter ---

    [Fact]
    public void El_mesurador_guarda_el_pic_i_es_posa_a_zero_en_llegir_lo()
    {
        var meter = new PeakMeter();
        meter.AddInterleaved([0.1f, -0.5f, -0.3f, 0.2f], 2);
        meter.Add(0.2f, 0.1f);

        Assert.Equal((0.3f, 0.5f), meter.Take());
        Assert.Equal((0f, 0f), meter.Take());
    }

    [Fact]
    public void Un_sol_canal_compta_per_als_dos()
    {
        var meter = new PeakMeter();
        meter.AddInterleaved([0.4f, -0.6f], 1);

        Assert.Equal((0.6f, 0.6f), meter.Take());
    }

    [Fact]
    public void Conversio_a_dbfs()
    {
        Assert.Equal(0, PeakMeter.ToDb(1f));
        Assert.Equal(-6.0, PeakMeter.ToDb(0.5f));
        Assert.Equal(-90, PeakMeter.ToDb(0f));
    }

    // --- Volum del reproductor ---

    [Fact]
    public void El_volum_abaixa_el_que_surt_i_el_mesurador_ho_reflecteix()
    {
        using var player = PlayingPlayer();
        _output!.Pump(4800); // a 0 dB
        Assert.Equal(0.2f, player.TakePeaks().Right, 2);

        player.SetVolume(-6);
        _output.Pump(480);   // bloc de transició (rampa)
        player.TakePeaks();
        var samples = _output.Pump(4800);

        Assert.Equal(-6.0, player.VolumeDb);
        Assert.Equal(0.1f * 0.501f, MaxAbs(samples, 0), 3);
        Assert.Equal(0.2f * 0.501f, MaxAbs(samples, 1), 3);
        Assert.Equal(0.2f * 0.501f, player.TakePeaks().Right, 3);
    }

    [Fact]
    public void Al_minim_del_fader_hi_ha_silenci()
    {
        using var player = PlayingPlayer();
        player.SetVolume(-60);
        _output!.Pump(480);
        var samples = _output.Pump(4800);

        Assert.Equal(0f, samples.Max(Math.Abs));
    }

    [Fact]
    public void El_volum_es_mante_en_carregar_una_altra_pista_i_es_limita_al_marge()
    {
        using var player = PlayingPlayer();
        player.SetVolume(-12);
        player.Stop();
        player.Load(TestAudio.CreateWav(48000, 2, 1.0));
        player.Play();
        Thread.Sleep(100);
        var samples = _output!.Pump(4800);

        Assert.Equal(0.1f * Player.GainFor(-12), MaxAbs(samples, 0), 3);

        player.SetVolume(+10);
        Assert.Equal(0, player.VolumeDb);
        player.SetVolume(-100);
        Assert.Equal(Player.MinVolumeDb, player.VolumeDb);
    }

    // --- Mesurador de l'enregistrador (sempre actiu) ---

    [Fact]
    public void L_entrada_s_obre_en_triar_la_i_el_mesurador_funciona_sense_enregistrar()
    {
        using var recorder = new Recorder("1", _ => _input = new FakeInput());
        recorder.SetDevice(InputId);

        Assert.True(_input!.IsRunning);
        Assert.True(recorder.InputActive);
        Assert.Equal(RecorderState.Idle, recorder.State);

        _input.PushTone(0.1); // to de 0,3 de pic
        Assert.Equal(0.3f, recorder.Meter.Take().Left, 2);
        Assert.Equal(TimeSpan.Zero, recorder.Elapsed); // no s'enregistra res
    }

    [Fact]
    public void En_aturar_l_enregistrament_l_entrada_continua_oberta()
    {
        string folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "meters-" + Guid.NewGuid().ToString("N"));
        using var recorder = new Recorder("1", _ => _input = new FakeInput());
        recorder.SetDevice(InputId);
        var monitor = _input;

        recorder.Start(folder, "Prova");
        Assert.Same(monitor, _input); // fa servir la mateixa entrada: comença a l'instant
        _input!.PushTone(0.5);
        recorder.Stop();

        Assert.True(_input.IsRunning);
        Assert.Equal(0.5, recorder.Elapsed.TotalSeconds, 3);
        _input.PushTone(0.5); // després d'aturar, només va al mesurador
        Assert.Equal(0.5, recorder.Elapsed.TotalSeconds, 3);
    }

    [Fact]
    public void Perdre_l_entrada_sense_enregistrar_no_es_un_error_i_es_torna_a_obrir()
    {
        int opened = 0;
        using var recorder = new Recorder("1", _ => { opened++; return _input = new FakeInput(); });
        recorder.SetDevice(InputId);

        _input!.Fail("device invalidated");
        recorder.Tick();

        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.False(recorder.InputActive);
        Assert.True(_input.Disposed);

        Thread.Sleep(3100); // es torna a provar al cap de 3 s
        recorder.Tick();

        Assert.True(recorder.InputActive);
        Assert.Equal(2, opened);
    }

    [Fact]
    public void Si_l_entrada_no_es_pot_obrir_no_hi_ha_error_fins_que_s_enregistra()
    {
        using var recorder = new Recorder("1", _ => throw new AudioDeviceException("Entrada no disponible."));
        recorder.SetDevice(InputId);

        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.False(recorder.InputActive);
    }

    // --- Protocol ---

    [Fact]
    public void Ordre_setVolume_i_esdeveniment_meters()
    {
        var state = new EngineState([new Player("1", _ => new FakeOutput())], [new Recorder("1", _ => _input = new FakeInput())]);
        var router = new CommandRouter(state, () => { }, () => []);
        router.Handle($$"""{ "v": 1, "id": 1, "cmd": "recorder.setDevice", "recorder": "1", "deviceId": "{{InputId}}" }""");

        var r = router.Handle("""{ "v": 1, "id": 2, "cmd": "player.setVolume", "player": "1", "db": -3.5 }""");
        Assert.Equal(-3.5, r["result"]!["player"]!["volumeDb"]!.GetValue<double>());

        var bad = router.Handle("""{ "v": 1, "id": 3, "cmd": "player.setVolume", "player": "1", "db": "fort" }""");
        Assert.Equal("bad_params", bad["error"]!["code"]!.GetValue<string>());

        _input!.Push([0.5f, 0.25f]);
        var ev = MetersJson.Event(state.Players, state.Recorders);
        Assert.Equal("meters", ev["ev"]!.GetValue<string>());
        Assert.Equal(-90, ev["players"]!["1"]![0]!.GetValue<double>());
        Assert.Equal(-6.0, ev["recorders"]!["1"]![0]!.GetValue<double>());
        Assert.Equal(-12.0, ev["recorders"]!["1"]![1]!.GetValue<double>());

        var stateGet = router.Handle("""{ "v": 1, "id": 4, "cmd": "state.get" }""");
        Assert.True(stateGet["result"]!["recorders"]![0]!["inputActive"]!.GetValue<bool>());
    }
}
