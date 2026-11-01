using System.Text.Json.Nodes;
using OnaBages.Engine;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

public class CommandRouterTests
{
    private static CommandRouter NewRouter(Action? onShutdown = null) =>
        new(new EngineState(), onShutdown ?? (() => { }));

    private static string ErrorCode(JsonObject response) =>
        response["error"]!["code"]!.GetValue<string>();

    [Fact]
    public void Hello_retorna_nom_versio_i_protocol()
    {
        var r = NewRouter().Handle("""{ "v": 1, "id": 1, "cmd": "engine.hello" }""");

        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.Equal(1, r["id"]!.GetValue<int>());
        Assert.Equal(Messages.ProtocolVersion, r["result"]!["protocol"]!.GetValue<int>());
        Assert.Equal(EngineInfo.Name, r["result"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void StateGet_retorna_estat_del_motor()
    {
        var r = NewRouter().Handle("""{ "v": 1, "id": 2, "cmd": "state.get" }""");

        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.NotNull(r["result"]!["engine"]!["uptime"]);
    }

    [Fact]
    public void Ordre_desconeguda_retorna_error_amb_el_mateix_id()
    {
        var r = NewRouter().Handle("""{ "v": 1, "id": 7, "cmd": "res.de.res" }""");

        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Equal(7, r["id"]!.GetValue<int>());
        Assert.Equal("unknown_command", ErrorCode(r));
    }

    [Fact]
    public void Versio_incorrecta_es_rebutja()
    {
        var r = NewRouter().Handle("""{ "v": 99, "id": 3, "cmd": "engine.hello" }""");

        Assert.Equal("protocol_version", ErrorCode(r));
    }

    [Fact]
    public void Sense_versio_es_rebutja()
    {
        var r = NewRouter().Handle("""{ "id": 3, "cmd": "engine.hello" }""");

        Assert.Equal("protocol_version", ErrorCode(r));
    }

    [Theory]
    [InlineData("això no és json")]
    [InlineData("[1, 2, 3]")]
    public void Missatge_no_valid_es_rebutja(string text)
    {
        var r = NewRouter().Handle(text);

        Assert.Equal("bad_json", ErrorCode(r));
    }

    [Fact]
    public void Sense_cmd_es_rebutja()
    {
        var r = NewRouter().Handle("""{ "v": 1, "id": 4 }""");

        Assert.Equal("missing_cmd", ErrorCode(r));
    }

    [Fact]
    public async Task Shutdown_respon_i_despres_atura_el_motor()
    {
        var stopped = new TaskCompletionSource();
        var router = NewRouter(() => stopped.TrySetResult());

        var r = router.Handle("""{ "v": 1, "id": 5, "cmd": "engine.shutdown" }""");

        Assert.True(r["ok"]!.GetValue<bool>());
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
