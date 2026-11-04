using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>Ordres recorder.* i protecció d'engine.shutdown, amb entrades falses.</summary>
public class RecorderCommandsTests
{
    private const string DeviceId = "{0.0.1.00000000}.{proves}";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "cmd-" + Guid.NewGuid().ToString("N"));
    private readonly EngineState _state;
    private readonly CommandRouter _router;
    private bool _shutdownRequested;

    public RecorderCommandsTests()
    {
        _state = new EngineState([], [new Recorder("1", _ => new FakeInput()), new Recorder("2", _ => new FakeInput())]);
        _router = new CommandRouter(_state, () => _shutdownRequested = true, () => []);
    }

    private JsonObject Send(string json) => _router.Handle(json);

    private static string ErrorCode(JsonObject r) => r["error"]!["code"]!.GetValue<string>();

    private static string RecorderState(JsonObject r) => r["result"]!["recorder"]!["state"]!.GetValue<string>();

    private string Folder => _folder.Replace("\\", "\\\\");

    private void StartRecording(string recorder = "1")
    {
        Send($$"""{ "v": 1, "id": 1, "cmd": "recorder.setDevice", "recorder": "{{recorder}}", "deviceId": "{{DeviceId}}" }""");
        var r = Send($$"""{ "v": 1, "id": 2, "cmd": "recorder.start", "recorder": "{{recorder}}", "folder": "{{Folder}}", "name": "Programa" }""");
        Assert.Equal("recording", RecorderState(r));
    }

    [Fact]
    public void Enregistrar_i_aturar()
    {
        StartRecording();
        var file = _state.FindRecorder("1")!.FilePath!;

        var r = Send("""{ "v": 1, "id": 3, "cmd": "recorder.stop", "recorder": "1" }""");

        Assert.Equal("pending", RecorderState(r));
        Assert.Equal(file, r["result"]!["recorder"]!["file"]!.GetValue<string>());
        Assert.True(File.Exists(file));
        Assert.False(File.Exists(file + ".rec"));
    }

    [Fact]
    public void Sense_carpeta_fa_servir_la_carpeta_per_defecte()
    {
        Send($$"""{ "v": 1, "id": 1, "cmd": "recorder.setDevice", "recorder": "2", "deviceId": "{{DeviceId}}" }""");
        var r = Send("""{ "v": 1, "id": 2, "cmd": "recorder.start", "recorder": "2" }""");
        var file = r["result"]!["recorder"]!["file"]!.GetValue<string>();
        Send("""{ "v": 1, "id": 3, "cmd": "recorder.stop", "recorder": "2" }""");

        Assert.StartsWith(Audio.Recorder.DefaultFolder, file);
        Assert.Contains("Enregistrador 2_", Path.GetFileName(file));
        File.Delete(file);
    }

    [Fact]
    public void Carpeta_relativa_dona_bad_params()
    {
        Send($$"""{ "v": 1, "id": 1, "cmd": "recorder.setDevice", "recorder": "1", "deviceId": "{{DeviceId}}" }""");

        var r = Send("""{ "v": 1, "id": 2, "cmd": "recorder.start", "recorder": "1", "folder": "enregistraments" }""");

        Assert.Equal("bad_params", ErrorCode(r));
    }

    [Fact]
    public void Ordres_no_permeses_donen_bad_state()
    {
        Assert.Equal("bad_state", ErrorCode(Send("""{ "v": 1, "id": 1, "cmd": "recorder.start", "recorder": "1" }""")));
        Assert.Equal("bad_state", ErrorCode(Send("""{ "v": 1, "id": 1, "cmd": "recorder.stop", "recorder": "1" }""")));
        Assert.Equal("bad_params", ErrorCode(Send("""{ "v": 1, "id": 1, "cmd": "recorder.stop", "recorder": "9" }""")));
    }

    [Fact]
    public void Shutdown_es_rebutja_mentre_s_enregistra()
    {
        StartRecording();

        var r = Send("""{ "v": 1, "id": 5, "cmd": "engine.shutdown" }""");

        Assert.Equal("recording_active", ErrorCode(r));
        Assert.False(_shutdownRequested);
        Assert.Equal(Audio.RecorderState.Recording, _state.FindRecorder("1")!.State);
        Send("""{ "v": 1, "id": 6, "cmd": "recorder.stop", "recorder": "1" }""");
    }

    [Fact]
    public async Task Shutdown_amb_force_s_accepta_mentre_s_enregistra()
    {
        StartRecording();

        var r = Send("""{ "v": 1, "id": 5, "cmd": "engine.shutdown", "force": true }""");

        Assert.True(r["ok"]!.GetValue<bool>());
        // El motor s'atura 200 ms després de respondre. Amb l'ordinador carregat (moltes proves alhora)
        // pot trigar més: s'espera fins a 5 s.
        for (int i = 0; i < 50 && !_shutdownRequested; i++)
            await Task.Delay(100);
        Assert.True(_shutdownRequested);
        _state.FindRecorder("1")!.Dispose(); // el motor ho fa en aturar-se
        Assert.False(File.Exists(_state.FindRecorder("1")!.FilePath + ".rec"));
    }

    [Fact]
    public void State_get_inclou_els_enregistradors()
    {
        StartRecording("2");

        var r = Send("""{ "v": 1, "id": 1, "cmd": "state.get" }""");
        var recorders = r["result"]!["recorders"]!.AsArray();

        Assert.Equal(2, recorders.Count);
        Assert.Equal("idle", recorders[0]!["state"]!.GetValue<string>());
        Assert.Equal("recording", recorders[1]!["state"]!.GetValue<string>());
        Assert.Equal(320, recorders[1]!["bitrateKbps"]!.GetValue<int>());
        Send("""{ "v": 1, "id": 2, "cmd": "recorder.stop", "recorder": "2" }""");
    }

    [Fact]
    public void Esdeveniment_de_progres_nomes_inclou_els_que_enregistren()
    {
        StartRecording("1");

        var ev = RecorderJson.ProgressEvent(_state.Recorders);

        Assert.Equal("recorder.progress", ev["ev"]!.GetValue<string>());
        var list = ev["recorders"]!.AsArray();
        Assert.Equal("1", Assert.Single(list)!["id"]!.GetValue<string>());
        Send("""{ "v": 1, "id": 2, "cmd": "recorder.stop", "recorder": "1" }""");
    }
}
