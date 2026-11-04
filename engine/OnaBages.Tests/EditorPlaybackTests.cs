using System.Text.Json.Nodes;
using NAudio.Wave;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>Reproducció de l'editor (fase 3, pas 3): sona la versió editada, per una sortida pròpia.</summary>
public class EditorPlaybackTests : IDisposable
{
    private const int Rate = 48000;
    private const string Output = "{0.0.0.00000000}.{sortida-editor}";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "edplay-" + Guid.NewGuid().ToString("N"));
    private FakeOutput? _output;

    public EditorPlaybackTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private IAudioOutput OpenFake(string id)
    {
        _output = new FakeOutput(Rate);
        return _output;
    }

    /// <summary>WAV de 3 s on cada segon té un nivell diferent (0,1 · 0,5 · 0,9): així se sap quin tros sona.</summary>
    private string CreateLevels()
    {
        string path = Path.Combine(_folder, "nivells.wav");
        float[] levels = [0.1f, 0.5f, 0.9f];
        var samples = new float[Rate * 3 * 2];
        for (int i = 0; i < Rate * 3; i++)
            samples[i * 2] = samples[i * 2 + 1] = levels[i / Rate];
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2));
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    /// <summary>Fa sonar fins al final i retorna el nivell de cada bloc de 10 ms.</summary>
    private List<float> PumpAll(EditorPlayer player)
    {
        var levels = new List<float>();
        for (int i = 0; i < 1000 && player.State == EditorPlayState.Playing; i++)
        {
            float[] block = _output!.Pump(Rate / 100);
            levels.Add(block[0]);
            if (i % 5 == 0) Thread.Sleep(2); // deixa temps al fil de lectura
            player.Tick();
        }
        return levels;
    }

    [Fact]
    public void Sona_nomes_el_que_es_queda()
    {
        using var player = new EditorPlayer(OpenFake);

        // Versió editada: el primer segon i el tercer (el segon s'ha eliminat).
        player.Play(Output, CreateLevels(), [new PlayPiece(0, 1), new PlayPiece(2, 1)], editedStart: 0);
        var levels = PumpAll(player).Where(l => l != 0).ToList(); // sense el silenci final

        Assert.Equal(EditorPlayState.Stopped, player.State);
        Assert.DoesNotContain(levels, l => Math.Abs(l - 0.5f) < 0.01f);
        Assert.InRange(levels.Count(l => Math.Abs(l - 0.1f) < 0.01f), 95, 105); // ≈ 1 s
        Assert.InRange(levels.Count(l => Math.Abs(l - 0.9f) < 0.01f), 95, 105); // ≈ 1 s
        Assert.Equal(2.0, player.Position, 3); // s'ha aturat al final
    }

    [Fact]
    public void La_posicio_es_de_la_versio_editada()
    {
        using var player = new EditorPlayer(OpenFake);

        player.Play(Output, CreateLevels(), [new PlayPiece(2, 1)], editedStart: 1.0);
        _output!.Pump(Rate / 2); // mig segon

        Assert.Equal(EditorPlayState.Playing, player.State);
        Assert.Equal(1.5, player.Position, 2);
    }

    [Fact]
    public void Stop_deixa_la_posicio_on_era()
    {
        using var player = new EditorPlayer(OpenFake);
        player.Play(Output, CreateLevels(), [new PlayPiece(0, 3)], editedStart: 0);
        _output!.Pump(Rate / 4);

        player.Stop();

        Assert.Equal(EditorPlayState.Stopped, player.State);
        Assert.Equal(0.25, player.Position, 2);
        Assert.True(_output.Disposed);
    }

    [Fact]
    public void Si_la_sortida_cau_queda_en_error()
    {
        using var player = new EditorPlayer(OpenFake);
        player.Play(Output, CreateLevels(), [new PlayPiece(0, 3)], editedStart: 0);

        _output!.Fail("desconnectat");
        player.Tick();

        Assert.Equal(EditorPlayState.Error, player.State);
        Assert.Contains("sortida de l'editor", player.ErrorMessage);
    }

    [Fact]
    public void Si_la_sortida_no_s_obre_queda_en_error()
    {
        using var player = new EditorPlayer(_ => throw new AudioDeviceException("No es pot obrir la sortida."));

        player.Play(Output, CreateLevels(), [new PlayPiece(0, 1)], editedStart: 0);

        Assert.Equal(EditorPlayState.Error, player.State);
    }

    // --- Ordres ---

    private string CreateMp3(double seconds)
    {
        string wav = TestAudio.CreateWav(Rate, 2, seconds);
        string path = Path.Combine(_folder, "prova.mp3");
        using (var source = AudioFileSource.Open(wav, Rate))
        using (var encoder = new LameEncoder(Rate, 320))
        using (var file = File.Create(path))
        {
            var buffer = new float[Rate];
            int read;
            while ((read = source.Read(buffer)) > 0) file.Write(encoder.Encode(buffer, read));
            file.Write(encoder.Flush());
        }
        File.Delete(wav);
        return path;
    }

    private (EngineState State, CommandRouter Router) Engine(bool withConfig = false)
    {
        var state = new EngineState(editor: new EditorSession(Path.Combine(_folder, "cache"), OpenFake));
        var devices = new List<AudioDevice> { new(Output, "Auriculars editor", DeviceKind.Output, DeviceStatus.Active, null) };
        Engine.Data.ConfigStore? config = null;
        if (withConfig)
        {
            config = new Engine.Data.ConfigStore(new Engine.Data.ConfigRepository(Path.Combine(_folder, "config.json")));
            config.Load();
        }
        return (state, new CommandRouter(state, () => { }, () => devices, config: config));
    }

    private static void OpenAndWait(EngineState state, string path)
    {
        state.Editor.Open(path);
        for (int i = 0; i < 200 && (state.Editor.Busy is not null || state.Editor.Edit is null); i++) Thread.Sleep(25);
    }

    private static JsonObject Send(CommandRouter router, string json) => router.Handle(json);

    [Fact]
    public void Sense_cap_sortida_no_es_pot_reproduir()
    {
        var (state, router) = Engine();
        OpenAndWait(state, CreateMp3(2));

        var r = Send(router, """{ "v": 1, "id": 1, "cmd": "editor.play" }""");
        state.Editor.Dispose();

        Assert.Equal("bad_state", r["error"]!["code"]!.GetValue<string>());
        Assert.Contains("sortida", r["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void Per_defecte_sona_per_la_sortida_de_la_preescolta()
    {
        var (state, router) = Engine();
        state.Cue.SetDevice(Output);
        OpenAndWait(state, CreateMp3(2));

        var r = Send(router, """{ "v": 1, "id": 1, "cmd": "editor.play", "from": 0.5 }""");
        var playback = r["result"]!["editor"]!["playback"]!;
        var stop = Send(router, """{ "v": 1, "id": 2, "cmd": "editor.stop" }""");
        state.Editor.Dispose();

        Assert.Equal("playing", playback["state"]!.GetValue<string>());
        Assert.Equal(0.504, playback["position"]!.GetValue<double>(), 3); // ajustat al fotograma (21 × 24 ms)
        Assert.Null(playback["output"]);
        Assert.Equal("stopped", stop["result"]!["editor"]!["playback"]!["state"]!.GetValue<string>());
    }

    [Fact]
    public void Es_pot_triar_una_sortida_propia_i_tornar_a_la_de_la_preescolta()
    {
        var (state, router) = Engine(withConfig: true);
        OpenAndWait(state, CreateMp3(2));

        var set = Send(router, $$"""{ "v": 1, "id": 1, "cmd": "editor.setDevice", "deviceId": "{{Output}}" }""");
        var play = Send(router, """{ "v": 1, "id": 2, "cmd": "editor.play" }""");
        var back = Send(router, """{ "v": 1, "id": 3, "cmd": "editor.setDevice", "deviceId": null }""");
        state.Editor.Dispose();

        Assert.Equal("Auriculars editor", set["result"]!["editor"]!["playback"]!["output"]!["name"]!.GetValue<string>());
        Assert.Equal("playing", play["result"]!["editor"]!["playback"]!["state"]!.GetValue<string>());
        Assert.Null(back["result"]!["editor"]!["playback"]!["output"]);
        Assert.Equal("stopped", back["result"]!["editor"]!["playback"]!["state"]!.GetValue<string>()); // canviar la sortida atura
    }

    [Fact]
    public void Una_sortida_que_no_existeix_dona_bad_params()
    {
        var (state, router) = Engine(withConfig: true);

        var r = Send(router, """{ "v": 1, "id": 1, "cmd": "editor.setDevice", "deviceId": "no-existeix" }""");

        Assert.Equal("bad_params", r["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public void Editar_mentre_sona_atura_la_reproduccio()
    {
        var (state, router) = Engine();
        state.Cue.SetDevice(Output);
        OpenAndWait(state, CreateMp3(2));
        Send(router, """{ "v": 1, "id": 1, "cmd": "editor.play" }""");

        var r = Send(router, """{ "v": 1, "id": 2, "cmd": "editor.delete", "start": 0, "end": 0.5 }""");
        state.Editor.Dispose();

        Assert.Equal("stopped", r["result"]!["editor"]!["playback"]!["state"]!.GetValue<string>());
    }
}
