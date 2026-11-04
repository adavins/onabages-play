using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>Pas 8 de la fase 2: preescolta (especificació 7), amb sortides falses.</summary>
public class CueTests : IDisposable
{
    private const string CueOut = "{auriculars}";
    private const string AntennaOut = "{antena}";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "cue-" + Guid.NewGuid().ToString("N"));
    private FakeOutput? _cueOutput;

    public CueTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private CuePlayer NewCue()
    {
        var cue = new CuePlayer(_ => _cueOutput = new FakeOutput());
        cue.SetDevice(CueOut);
        return cue;
    }

    private static string Wav(double seconds = 2.0) => TestAudio.CreateWav(48000, 2, seconds);

    /// <summary>Motor amb un reproductor i la preescolta (sortides falses), i el seu CommandRouter.</summary>
    private (EngineState State, CommandRouter Router, ConfigStore Config) NewEngine()
    {
        var state = new EngineState(
            [new Player("1", _ => new FakeOutput(), new TrackAnalyzer(synchronous: true))],
            [],
            new CuePlayer(_ => _cueOutput = new FakeOutput()));
        var repository = new ConfigRepository(Path.Combine(_folder, "config.json"));
        var config = new ConfigStore(repository);
        var hardware = new HardwareAssignments(state, config);
        var devices = new List<AudioDevice>
        {
            new(CueOut, "Auriculars", DeviceKind.Output, DeviceStatus.Active, null),
            new(AntennaOut, "Antena", DeviceKind.Output, DeviceStatus.Active, null),
        };
        var router = new CommandRouter(state, () => { }, () => devices, hardware, config);
        return (state, router, config);
    }

    private static JsonObject Send(CommandRouter router, string json)
    {
        var response = router.Handle(json);
        Assert.True(response["ok"]!.GetValue<bool>(), response.ToJsonString());
        return response["result"]!.AsObject();
    }

    private static string Escape(string path) => path.Replace("\\", "\\\\");

    // --- La classe CuePlayer ---

    [Fact]
    public void Fa_sonar_una_pista_per_la_seva_sortida()
    {
        using var cue = NewCue();
        string wav = Wav();

        cue.Play(wav);

        Assert.Equal(CueState.Playing, cue.State);
        Assert.Equal(wav, cue.FilePath);
        Assert.InRange(cue.Duration.TotalSeconds, 1.9, 2.1);
        Assert.True(_cueOutput!.IsPlaying);
    }

    [Fact]
    public void Pot_començar_des_d_una_posicio()
    {
        using var cue = NewCue();
        string wav = Wav(4);

        cue.Play(wav, TimeSpan.FromSeconds(3));

        Assert.Equal(CueState.Playing, cue.State);
        Assert.InRange(cue.Position.TotalSeconds, 2.9, 3.2);
    }

    [Fact]
    public void Una_pista_nova_substitueix_la_que_sonava()
    {
        using var cue = NewCue();
        cue.Play(Wav());
        string second = Wav(1);

        cue.Play(second);

        Assert.Equal(CueState.Playing, cue.State);
        Assert.Equal(second, cue.FilePath);
        Assert.InRange(cue.Duration.TotalSeconds, 0.9, 1.1);
    }

    [Fact]
    public void Stop_atura_pero_recorda_la_pista_per_tornar_hi()
    {
        using var cue = NewCue();
        string wav = Wav();
        cue.Play(wav);

        cue.Stop();

        Assert.Equal(CueState.Stopped, cue.State);
        Assert.Equal(wav, cue.FilePath);
        Assert.InRange(cue.Duration.TotalSeconds, 1.9, 2.1);
        Assert.Equal(TimeSpan.Zero, cue.Position);

        // Saltar amb la preescolta aturada la torna a fer sonar des d'allà (botons de salt).
        cue.Seek(TimeSpan.FromSeconds(1));
        Assert.Equal(CueState.Playing, cue.State);
        Assert.InRange(cue.Position.TotalSeconds, 0.9, 1.2);

        // I Play sense pista, torna a fer sonar l'última.
        cue.Stop();
        cue.Play();
        Assert.Equal(CueState.Playing, cue.State);
    }

    [Fact]
    public void Quan_la_pista_s_acaba_s_atura()
    {
        using var cue = NewCue();
        cue.Play(Wav(0.2));

        for (int i = 0; i < 20 && cue.State == CueState.Playing; i++)
        {
            _cueOutput!.Pump(4800); // 0,1 s
            cue.Tick();
        }

        Assert.Equal(CueState.Stopped, cue.State);
    }

    [Fact]
    public void Sense_sortida_no_pot_sonar()
    {
        using var cue = new CuePlayer(_ => new FakeOutput());

        var ex = Assert.Throws<PlayerCommandException>(() => cue.Play(Wav()));
        Assert.Contains("sortida", ex.Message);
    }

    [Fact]
    public void Canviar_la_sortida_mentre_sona_l_atura()
    {
        using var cue = NewCue();
        cue.Play(Wav());

        cue.SetDevice("{altra}");

        Assert.Equal(CueState.Stopped, cue.State);
        Assert.Equal("{altra}", cue.DeviceId);
    }

    // --- Ordres i protocol ---

    [Fact]
    public void Ordres_cue_play_seek_stop()
    {
        var (state, router, _) = NewEngine();
        Send(router, $$"""{ "v": 1, "id": 1, "cmd": "cue.setDevice", "deviceId": "{{CueOut}}" }""");
        string wav = Wav(4);

        var played = Send(router, $$"""{ "v": 1, "id": 2, "cmd": "cue.play", "path": "{{Escape(wav)}}" }""");
        Assert.Equal("playing", played["cue"]!["state"]!.GetValue<string>());
        Assert.Equal(wav, played["cue"]!["file"]!.GetValue<string>());

        Send(router, """{ "v": 1, "id": 3, "cmd": "cue.seek", "position": 2.5 }""");
        Assert.InRange(state.Cue.Position.TotalSeconds, 2.4, 2.7);

        var stopped = Send(router, """{ "v": 1, "id": 4, "cmd": "cue.stop" }""");
        Assert.Equal("stopped", stopped["cue"]!["state"]!.GetValue<string>());

        // A state.get hi és, amb la pista recordada.
        var all = Send(router, """{ "v": 1, "id": 5, "cmd": "state.get" }""");
        Assert.Equal(wav, all["cue"]!["file"]!.GetValue<string>());
    }

    [Fact]
    public void La_preescolta_no_toca_cap_reproductor()
    {
        var (state, router, _) = NewEngine();
        var player = state.Players[0];
        player.SetDevice(AntennaOut);
        string onAir = Wav(4);
        player.Load(onAir);
        player.Play();
        Send(router, $$"""{ "v": 1, "id": 1, "cmd": "cue.setDevice", "deviceId": "{{CueOut}}" }""");

        Send(router, $$"""{ "v": 1, "id": 2, "cmd": "cue.play", "path": "{{Escape(Wav())}}" }""");

        Assert.Equal(PlayerState.Playing, player.State);
        Assert.Equal(onAir, player.FilePath);
        Assert.Single(player.Playlist.Items);
    }

    [Fact]
    public void Errors_de_parametres_i_d_estat()
    {
        var (_, router, _) = NewEngine();

        // Sense sortida: no pot sonar.
        var noDevice = router.Handle($$"""{ "v": 1, "id": 1, "cmd": "cue.play", "path": "{{Escape(Wav())}}" }""");
        Assert.Equal("bad_state", noDevice["error"]!["code"]!.GetValue<string>());

        var relative = router.Handle("""{ "v": 1, "id": 2, "cmd": "cue.play", "path": "pista.mp3" }""");
        Assert.Equal("bad_params", relative["error"]!["code"]!.GetValue<string>());

        var noPosition = router.Handle("""{ "v": 1, "id": 3, "cmd": "cue.seek" }""");
        Assert.Equal("bad_params", noPosition["error"]!["code"]!.GetValue<string>());

        // Un fitxer que no es pot llegir: error de la preescolta, amb el motiu.
        router.Handle($$"""{ "v": 1, "id": 4, "cmd": "cue.setDevice", "deviceId": "{{CueOut}}" }""");
        string broken = Path.Combine(_folder, "malmes.mp3");
        File.WriteAllBytes(broken, new byte[1000]);
        var unreadable = router.Handle($$"""{ "v": 1, "id": 5, "cmd": "cue.play", "path": "{{Escape(broken)}}" }""");
        Assert.Equal("player_error", unreadable["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public void Sortida_i_volum_es_desen_a_la_configuracio()
    {
        var (_, router, config) = NewEngine();

        Send(router, $$"""{ "v": 1, "id": 1, "cmd": "cue.setDevice", "deviceId": "{{CueOut}}" }""");
        var result = Send(router, """{ "v": 1, "id": 2, "cmd": "cue.setVolume", "db": -12 }""");

        Assert.Equal(-12, result["cue"]!["volumeDb"]!.GetValue<double>());
        var loaded = new ConfigRepository(Path.Combine(_folder, "config.json")).Load().Config;
        Assert.Equal(new DeviceRef(CueOut, "Auriculars"), loaded.Cue[EngineState.CueId]);
        Assert.Equal(-12, loaded.CueVolumeDb);
    }

    [Fact]
    public void En_arrencar_es_torna_a_assignar_la_sortida_desada()
    {
        var repository = new ConfigRepository(Path.Combine(_folder, "config.json"));
        var saved = new EngineConfig();
        saved.Cue[EngineState.CueId] = new DeviceRef(CueOut, "Auriculars");
        repository.Save(saved);

        var state = new EngineState([], [], new CuePlayer(_ => new FakeOutput()));
        var hardware = new HardwareAssignments(state, repository);
        var report = hardware.Start([new AudioDevice(CueOut, "Auriculars", DeviceKind.Output, DeviceStatus.Active, null)]);

        Assert.Equal(CueOut, state.Cue.DeviceId);
        var check = Assert.Single(report.Elements);
        Assert.Equal(ElementKind.Cue, check.Kind);
        Assert.Equal("cue", HardwareJson.Report(report)["elements"]![0]!["element"]!.GetValue<string>());
    }

    [Fact]
    public void Avisa_si_comparteix_sortida_amb_un_reproductor()
    {
        var (state, router, _) = NewEngine();
        state.Players[0].SetDevice(AntennaOut);

        Send(router, $$"""{ "v": 1, "id": 1, "cmd": "cue.setDevice", "deviceId": "{{CueOut}}" }""");
        Assert.False(state.CueSharesPlayerOutput);

        var shared = Send(router, $$"""{ "v": 1, "id": 2, "cmd": "cue.setDevice", "deviceId": "{{AntennaOut}}" }""");
        Assert.True(shared["cue"]!["sharesPlayerOutput"]!.GetValue<bool>());
    }

    [Fact]
    public void El_mesurador_inclou_la_preescolta()
    {
        var (state, _, _) = NewEngine();

        var ev = MetersJson.Event(state.Players, state.Recorders, state.Cue);

        Assert.Equal(2, ev["cue"]!.AsArray().Count);
    }

    [Fact]
    public void La_posicio_porta_la_durada_i_el_format()
    {
        using var cue = new CuePlayer(_ => new FakeOutput(), new TrackAnalyzer(synchronous: true));
        cue.SetDevice(CueOut);
        cue.Play(Wav());

        var ev = CueJson.PositionEvent(cue);

        Assert.Equal("cue.position", ev["ev"]!.GetValue<string>());
        Assert.InRange(ev["duration"]!.GetValue<double>(), 1.9, 2.1);
        Assert.Contains("WAV", ev["format"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        // Aturada, el format de l'última pista es conserva.
        cue.Stop();
        Assert.NotNull(cue.Format);
    }
}
