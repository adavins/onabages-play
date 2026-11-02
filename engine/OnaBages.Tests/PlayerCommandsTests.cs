using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>Ordres player.* del protocol, amb reproductors de sortida falsa.</summary>
public class PlayerCommandsTests
{
    private const string DeviceId = "{0.0.0.00000000}.{proves}";

    private readonly CommandRouter _router;
    private readonly EngineState _state;

    public PlayerCommandsTests()
    {
        _state = new EngineState([new Player("1", _ => new FakeOutput()), new Player("2", _ => new FakeOutput())]);
        _router = new CommandRouter(_state, () => { }, () => []);
    }

    private JsonObject Send(string json) => _router.Handle(json);

    private static string ErrorCode(JsonObject r) => r["error"]!["code"]!.GetValue<string>();

    private static string PlayerState(JsonObject r) => r["result"]!["player"]!["state"]!.GetValue<string>();

    private static string Escape(string path) => path.Replace("\\", "\\\\");

    private void LoadReady(string player = "1")
    {
        Send($$"""{ "v": 1, "id": 1, "cmd": "player.setDevice", "player": "{{player}}", "deviceId": "{{DeviceId}}" }""");
        var wav = Escape(TestAudio.CreateWav(48000, 2, 1.0));
        Send($$"""{ "v": 1, "id": 2, "cmd": "player.load", "player": "{{player}}", "path": "{{wav}}" }""");
    }

    [Fact]
    public void Carregar_i_reproduir()
    {
        var r = Send($$"""{ "v": 1, "id": 1, "cmd": "player.setDevice", "player": "1", "deviceId": "{{DeviceId}}" }""");
        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.Equal("empty", PlayerState(r));

        var wav = Escape(TestAudio.CreateWav(48000, 2, 1.0));
        r = Send($$"""{ "v": 1, "id": 2, "cmd": "player.load", "player": "1", "path": "{{wav}}" }""");
        Assert.Equal("ready", PlayerState(r));
        Assert.Equal(1.0, r["result"]!["player"]!["duration"]!.GetValue<double>());
        Assert.Equal(DeviceId, r["result"]!["player"]!["deviceId"]!.GetValue<string>());

        r = Send("""{ "v": 1, "id": 3, "cmd": "player.play", "player": "1" }""");
        Assert.Equal("playing", PlayerState(r));

        r = Send("""{ "v": 1, "id": 4, "cmd": "player.pause", "player": "1" }""");
        Assert.Equal("paused", PlayerState(r));

        r = Send("""{ "v": 1, "id": 5, "cmd": "player.stop", "player": "1" }""");
        Assert.Equal("ready", PlayerState(r));
    }

    [Fact]
    public void Els_reproductors_son_independents()
    {
        LoadReady("1");
        Send("""{ "v": 1, "id": 3, "cmd": "player.play", "player": "1" }""");

        Assert.Equal(Audio.PlayerState.Playing, _state.FindPlayer("1")!.State);
        Assert.Equal(Audio.PlayerState.Empty, _state.FindPlayer("2")!.State);
    }

    [Fact]
    public void Reproductor_inexistent_o_sense_camp_dona_bad_params()
    {
        Assert.Equal("bad_params", ErrorCode(Send("""{ "v": 1, "id": 1, "cmd": "player.play", "player": "9" }""")));
        Assert.Equal("bad_params", ErrorCode(Send("""{ "v": 1, "id": 1, "cmd": "player.play" }""")));
        Assert.Equal("bad_params", ErrorCode(Send("""{ "v": 1, "id": 1, "cmd": "player.load", "player": "1" }""")));
    }

    [Fact]
    public void Ordre_no_permesa_en_l_estat_actual_dona_bad_state()
    {
        var r = Send("""{ "v": 1, "id": 7, "cmd": "player.play", "player": "1" }""");

        Assert.Equal("bad_state", ErrorCode(r));
        Assert.Equal(7, r["id"]!.GetValue<int>());
        Assert.Contains("cap pista", r["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void Fitxer_que_no_existeix_dona_player_error_amb_el_motiu()
    {
        Send($$"""{ "v": 1, "id": 1, "cmd": "player.setDevice", "player": "1", "deviceId": "{{DeviceId}}" }""");

        var r = Send("""{ "v": 1, "id": 2, "cmd": "player.load", "player": "1", "path": "C:\\no\\existeix.mp3" }""");

        Assert.Equal("player_error", ErrorCode(r));
        Assert.Contains("No es troba", r["error"]!["message"]!.GetValue<string>());
        Assert.Equal(Audio.PlayerState.Error, _state.FindPlayer("1")!.State);
    }

    [Fact]
    public void Retry_sense_error_dona_bad_state()
    {
        LoadReady("1");

        var r = Send("""{ "v": 1, "id": 8, "cmd": "player.retry", "player": "1" }""");

        Assert.Equal("bad_state", ErrorCode(r));
    }

    [Fact]
    public void State_get_inclou_els_reproductors()
    {
        LoadReady("2");

        var r = Send("""{ "v": 1, "id": 1, "cmd": "state.get" }""");
        var players = r["result"]!["players"]!.AsArray();

        Assert.Equal(2, players.Count);
        Assert.Equal("1", players[0]!["id"]!.GetValue<string>());
        Assert.Equal("empty", players[0]!["state"]!.GetValue<string>());
        Assert.Equal("ready", players[1]!["state"]!.GetValue<string>());
        Assert.EndsWith(".wav", players[1]!["file"]!.GetValue<string>());
    }

    [Fact]
    public void Esdeveniments_de_reproductor()
    {
        LoadReady("1");
        var player = _state.FindPlayer("1")!;

        var stateEv = PlayerJson.StateEvent(player);
        Assert.Equal("player.state", stateEv["ev"]!.GetValue<string>());
        Assert.Equal("ready", stateEv["player"]!["state"]!.GetValue<string>());

        var posEv = PlayerJson.PositionEvent(_state.Players);
        Assert.Equal("player.position", posEv["ev"]!.GetValue<string>());
        Assert.Equal(2, posEv["players"]!.AsArray().Count);
        Assert.Equal(0.0, posEv["players"]![0]!["position"]!.GetValue<double>());
    }
}
