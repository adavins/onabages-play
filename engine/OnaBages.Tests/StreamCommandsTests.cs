using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>
/// Ordres stream.* de l'emissor d'streaming (fase 5, pas 31c; D-143): la llista de servidors, què es desa al perfil,
/// què es recupera en arrencar, i que no es pot tancar el motor ni canviar de perfil mentre s'emet sense voler.
/// </summary>
public class StreamCommandsTests : IDisposable
{
    private const string LineIn = "{0.0.1.00000000}.{linia}";
    private const string Speakers = "{0.0.0.00000000}.{altaveus}";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "stream-" + Guid.NewGuid().ToString("N"));
    private int _id;

    public StreamCommandsTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static readonly List<AudioDevice> Devices =
    [
        new(LineIn, "Entrada de línia", DeviceKind.Input, DeviceStatus.Active, null),
        new(Speakers, "Altaveus", DeviceKind.Output, DeviceStatus.Active, null),
    ];

    private static Streamer NewStreamer() =>
        new(EngineState.StreamerId, _ => new FakeInput(), (_, _) => { }) { RetryDelay = TimeSpan.FromMilliseconds(100) };

    private (EngineState State, CommandRouter Router, ConfigRepository Repository) NewEngine()
    {
        var state = new EngineState([], [], streamer: NewStreamer());
        var repository = new ConfigRepository(Path.Combine(_folder, "config.json"));
        var config = new ConfigStore(repository);
        config.Load();
        var hardware = new HardwareAssignments(state, config);
        return (state, new CommandRouter(state, () => { }, () => Devices, hardware, config), repository);
    }

    private JsonObject Send(CommandRouter router, JsonObject fields)
    {
        var r = router.Handle(Message(fields));
        Assert.True(r["ok"]!.GetValue<bool>(), r.ToJsonString());
        return r["result"]!.AsObject();
    }

    private (string Code, string Message) Error(CommandRouter router, JsonObject fields)
    {
        var r = router.Handle(Message(fields));
        Assert.False(r["ok"]!.GetValue<bool>(), r.ToJsonString());
        return (r["error"]!["code"]!.GetValue<string>(), r["error"]!["message"]!.GetValue<string>());
    }

    private string Message(JsonObject fields)
    {
        var msg = new JsonObject { ["v"] = 1, ["id"] = ++_id };
        foreach (var (key, value) in fields) msg[key] = value?.DeepClone();
        return msg.ToJsonString();
    }

    private static JsonObject OnaBages(int port = 8000) => new()
    {
        ["name"] = "Ona Bages",
        ["type"] = "shoutcast",
        ["host"] = "https://onabagescast.radioca.st/;", // l'adreça de l'oient també serveix: se'n treu l'adreça
        ["port"] = port,
        ["password"] = "secreta",
    };

    [Fact]
    public void Servidors_la_contrasenya_es_desa_pero_no_s_envia()
    {
        var (state, router, repository) = NewEngine();

        var r = Send(router, new() { ["cmd"] = "stream.saveServer", ["server"] = OnaBages() });
        Assert.Equal("1", r["serverId"]!.GetValue<string>());
        var stream = r["stream"]!;
        Assert.Equal("1", stream["serverId"]!.GetValue<string>()); // el primer queda triat
        var server = stream["servers"]![0]!;
        Assert.Equal("onabagescast.radioca.st", server["host"]!.GetValue<string>());
        Assert.Equal("shoutcast", server["type"]!.GetValue<string>());
        Assert.True(server["hasPassword"]!.GetValue<bool>());
        Assert.DoesNotContain("secreta", r.ToJsonString());
        Assert.Equal("onabagescast.radioca.st", state.Streamer.Server!.Host);
        Assert.Equal("secreta", state.Streamer.Server.Password);

        var saved = Assert.Single(repository.Load().Config.StreamServers);
        Assert.Equal(new StreamServerSettings("1", "Ona Bages", StreamServerType.Shoutcast, "onabagescast.radioca.st", 8000, "secreta"), saved);

        // Canviar-lo sense enviar la contrasenya la conserva.
        var edit = OnaBages(9000);
        edit["id"] = "1";
        edit.Remove("password");
        Send(router, new() { ["cmd"] = "stream.saveServer", ["server"] = edit });
        Assert.Equal((9000, "secreta"), (repository.Load().Config.StreamServers[0].Port, repository.Load().Config.StreamServers[0].Password));

        // Un Icecast, amb el punt de muntatge.
        r = Send(router, new() { ["cmd"] = "stream.saveServer", ["server"] = new JsonObject
        {
            ["name"] = "Ràdio Súria", ["type"] = "icecast", ["host"] = "relay.example.com", ["port"] = 40287, ["mount"] = "radiosuriaHD.mp3",
        } });
        Assert.Equal("2", r["serverId"]!.GetValue<string>());
        Assert.Equal("1", r["stream"]!["serverId"]!.GetValue<string>()); // continua triat el primer
        Assert.Equal("/radiosuriaHD.mp3", r["stream"]!["servers"]![1]!["mount"]!.GetValue<string>());
        Assert.False(r["stream"]!["servers"]![1]!["hasPassword"]!.GetValue<bool>());

        Send(router, new() { ["cmd"] = "stream.selectServer", ["serverId"] = "2" });
        Assert.Equal(StreamServerType.Icecast, state.Streamer.Server!.Type);
        Assert.Equal("Ràdio Súria", state.Streamer.Info.Name);

        // Esborrar el triat tria el primer que queda.
        r = Send(router, new() { ["cmd"] = "stream.deleteServer", ["serverId"] = "2" });
        Assert.Equal("1", r["stream"]!["serverId"]!.GetValue<string>());
        Send(router, new() { ["cmd"] = "stream.deleteServer", ["serverId"] = "1" });
        Assert.Null(repository.Load().Config.Stream.ServerId);
        Assert.Null(state.Streamer.Server);
    }

    [Fact]
    public void Errors_de_parametres()
    {
        var (_, router, _) = NewEngine();
        JsonObject With(string key, JsonNode? value)
        {
            var s = OnaBages();
            s[key] = value;
            return new JsonObject { ["cmd"] = "stream.saveServer", ["server"] = s };
        }

        Assert.Equal("bad_params", Error(router, With("type", "rtmp")).Code);
        Assert.Equal("bad_params", Error(router, With("port", 70000)).Code);
        Assert.Equal("bad_params", Error(router, With("host", "")).Code);
        Assert.Contains("port a part", Error(router, With("host", "radio.example.com:8000")).Message);
        Assert.Equal("bad_params", Error(router, With("name", new string('x', 41))).Code);
        Assert.Equal("bad_params", Error(router, With("id", "99")).Code);
        Assert.Equal("bad_params", Error(router, new() { ["cmd"] = "stream.selectServer", ["serverId"] = "99" }).Code);
        Assert.Equal("bad_params", Error(router, new() { ["cmd"] = "stream.setDevice", ["deviceId"] = Speakers }).Code); // una sortida
        Assert.Equal("bad_params", Error(router, new() { ["cmd"] = "stream.setQuality", ["bitrateKbps"] = 100 }).Code);
        Assert.Equal("bad_params", Error(router, new() { ["cmd"] = "stream.setQuality", ["sampleRate"] = 22050 }).Code);
        Assert.Equal("bad_params", Error(router, new() { ["cmd"] = "stream.setTitle", ["title"] = new string('x', 121) }).Code);
        Assert.Equal("bad_params", Error(router, new() { ["cmd"] = "stream.setLimiter", ["enabled"] = true, ["ceilingDb"] = -10 }).Code);
        Assert.Equal("bad_params", Error(router, new() { ["cmd"] = "stream.setProcessing", ["preset"] = "no-existeix" }).Code);
        Assert.Equal("bad_state", Error(router, new() { ["cmd"] = "stream.start" }).Code); // sense font
        Assert.Equal("bad_state", Error(router, new() { ["cmd"] = "stream.stop" }).Code);  // no s'emet
    }

    [Fact]
    public void Els_ajustos_es_desen_al_perfil_i_s_apliquen_en_arrencar()
    {
        var (_, router, repository) = NewEngine();
        Send(router, new() { ["cmd"] = "stream.saveServer", ["server"] = OnaBages() });
        Send(router, new() { ["cmd"] = "stream.setDevice", ["deviceId"] = LineIn });
        Send(router, new() { ["cmd"] = "stream.setQuality", ["bitrateKbps"] = 192, ["sampleRate"] = 48000 });
        Send(router, new() { ["cmd"] = "stream.setTitle", ["title"] = " Directe des de la plaça " });
        Send(router, new() { ["cmd"] = "stream.setGain", ["db"] = 20 }); // al límit: +12
        Send(router, new() { ["cmd"] = "stream.setLeveler", ["enabled"] = true });
        Send(router, new() { ["cmd"] = "stream.setLimiter", ["enabled"] = true, ["ceilingDb"] = -2 });
        var r = Send(router, new() { ["cmd"] = "stream.setProcessing", ["preset"] = "sqrsoft-la-central" });
        Assert.True(r["stream"]!["processing"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("Directe des de la plaça", r["stream"]!["title"]!.GetValue<string>());

        var saved = repository.Load().Config;
        Assert.Equal(new DeviceRef(LineIn, "Entrada de línia"), saved.StreamDevice[EngineState.StreamerId]);
        Assert.Equal((192, 48000, false, "Directe des de la plaça", 12.0, true, true, -2.0),
            (saved.Stream.BitrateKbps, saved.Stream.SampleRate, saved.Stream.Mono, saved.Stream.Title, saved.Stream.GainDb,
             saved.Stream.Leveler, saved.Stream.Limiter, saved.Stream.LimiterCeilingDb));
        Assert.Equal(DynamicsSettings.SqrSoftLaCentral, saved.Stream.Dynamics);

        // Un motor nou amb aquest perfil: la font (maquinari) i la resta d'ajustos tornen a ser-hi.
        var state = new EngineState([], [], streamer: NewStreamer());
        var config = new ConfigStore(repository);
        var report = new HardwareAssignments(state, config).Start(Devices);
        CommandRouter.ApplyStreamSettings(config, state.Streamer);
        var s = state.Streamer;
        Assert.Equal(LineIn, s.DeviceId);
        Assert.Equal("stream", HardwareJson.Report(report)["elements"]![0]!["element"]!.GetValue<string>());
        Assert.Equal("Streaming", HardwareAssignments.ElementName(ElementKind.Streamer, EngineState.StreamerId));
        Assert.Equal(("onabagescast.radioca.st", "Ona Bages"), (s.Server!.Host, s.Info.Name));
        Assert.Equal((192, 48000, "Directe des de la plaça", 12.0), (s.BitrateKbps, s.SampleRate, s.Title, s.GainDb));
        Assert.True(s.LevelerEnabled);
        Assert.Equal(EngineConfig.DefaultLoudnessTargetLufs, s.LevelerTargetLufs);
        Assert.Equal((true, -2.0), (s.LimiterEnabled, s.LimiterCeilingDb));
        Assert.Equal(DynamicsSettings.SqrSoftLaCentral, s.Dynamics);
    }

    [Fact]
    public void Mentre_s_emet_no_es_pot_tancar_ni_canviar_de_perfil_sense_voler()
    {
        var (state, router, _) = NewEngine();
        Send(router, new() { ["cmd"] = "stream.setDevice", ["deviceId"] = LineIn });
        Assert.Contains("servidor", Error(router, new() { ["cmd"] = "stream.start" }).Message);

        // Un servidor on no hi ha ningú: es queda «connectant» i ho continua provant.
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
        Send(router, new() { ["cmd"] = "stream.saveServer", ["server"] = new JsonObject
        {
            ["name"] = "Prova", ["type"] = "icecast", ["host"] = "127.0.0.1", ["port"] = port,
        } });
        var r = Send(router, new() { ["cmd"] = "stream.start" });
        Assert.Equal("connecting", r["stream"]!["state"]!.GetValue<string>());
        Assert.True(state.IsStreaming);

        Assert.Equal("bad_state", Error(router, new() { ["cmd"] = "stream.setQuality", ["bitrateKbps"] = 192 }).Code);
        Assert.Equal("bad_state", Error(router, new() { ["cmd"] = "stream.selectServer", ["serverId"] = "1" }).Code);
        Assert.Equal("bad_state", Error(router, new() { ["cmd"] = "stream.deleteServer", ["serverId"] = "1" }).Code);
        Send(router, new() { ["cmd"] = "stream.setTitle", ["title"] = "Ara sí" }); // el títol sí
        Send(router, new() { ["cmd"] = "stream.setGain", ["db"] = -3 });          // i el fader

        var (code, message) = Error(router, new() { ["cmd"] = "engine.shutdown" });
        Assert.Equal("recording_active", code);
        Assert.Contains("streaming", message);
        Assert.Contains("streaming", Error(router, new() { ["cmd"] = "profile.switch", ["profile"] = "altre" }).Message);

        var stateJson = Send(router, new() { ["cmd"] = "state.get" });
        Assert.Equal("connecting", stateJson["stream"]!["state"]!.GetValue<string>());
        Assert.NotNull(stateJson["stream"]!["progress"]);
        var meters = MetersJson.Event([], [], streamer: state.Streamer);
        Assert.Equal(2, meters["stream"]!.AsArray().Count);
        Assert.NotNull(meters["streamReduction"]!["limiter"]);
        Assert.True(meters.ContainsKey("streamLeveler")); // null: el NIV no treballa

        r = Send(router, new() { ["cmd"] = "stream.stop" });
        Assert.Equal("idle", r["stream"]!["state"]!.GetValue<string>());
        Assert.False(state.IsStreaming);
    }
}
