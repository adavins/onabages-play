using System.Diagnostics;
using System.Text.Json.Nodes;
using NAudio.Wave;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>
/// Ordres cart.* de la botonera (fase 5, pas 28a2; D-127), què es desa al perfil, la sortida en arrencar
/// (com els altres elements) i el mesurador.
/// </summary>
public class CartCommandsTests : IDisposable
{
    private const string CartOut = "{0.0.0.00000000}.{botonera}";
    private const string AntennaOut = "{0.0.0.00000000}.{antena}";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "cart-" + Guid.NewGuid().ToString("N"));
    private int _id;

    public CartCommandsTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static readonly List<AudioDevice> Devices =
    [
        new(CartOut, "Botonera", DeviceKind.Output, DeviceStatus.Active, null),
        new(AntennaOut, "Antena", DeviceKind.Output, DeviceStatus.Active, null),
        new("{0.0.1.00000000}.{micro}", "Micro", DeviceKind.Input, DeviceStatus.Active, null),
    ];

    private (EngineState State, CommandRouter Router, ConfigRepository Repository) NewEngine()
    {
        var state = new EngineState(
            [new Player("1", _ => new FakeOutput(), new TrackAnalyzer(synchronous: true))], [],
            cart: new CartWall(_ => new FakeOutput()));
        var repository = new ConfigRepository(Path.Combine(_folder, "config.json"));
        var config = new ConfigStore(repository);
        config.Load();
        var hardware = new HardwareAssignments(state, config);
        return (state, new CommandRouter(state, () => { }, () => Devices, hardware, config), repository);
    }

    private JsonObject Send(CommandRouter router, JsonObject fields)
    {
        var msg = new JsonObject { ["v"] = 1, ["id"] = ++_id };
        foreach (var (key, value) in fields) msg[key] = value?.DeepClone();
        var r = router.Handle(msg.ToJsonString());
        Assert.True(r["ok"]!.GetValue<bool>(), r.ToJsonString());
        return r["result"]!.AsObject();
    }

    private string Error(CommandRouter router, JsonObject fields)
    {
        var msg = new JsonObject { ["v"] = 1, ["id"] = ++_id };
        foreach (var (key, value) in fields) msg[key] = value?.DeepClone();
        var r = router.Handle(msg.ToJsonString());
        Assert.False(r["ok"]!.GetValue<bool>(), r.ToJsonString());
        return r["error"]!["code"]!.GetValue<string>();
    }

    private string Wav(double seconds, string name)
    {
        string path = Path.Combine(_folder, name + ".wav");
        int frames = (int)(48000 * seconds);
        var samples = new float[frames * 2];
        for (int i = 0; i < frames; i++) samples[i * 2] = samples[i * 2 + 1] = 0.3f * (float)Math.Sin(2 * Math.PI * 440 * i / 48000);
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private static void WaitReady(EngineState state, int page, int index)
    {
        var watch = Stopwatch.StartNew();
        while (state.Cart.ButtonAt(page, index)?.Status == CartButtonStatus.Loading && watch.Elapsed < TimeSpan.FromSeconds(10))
            Thread.Sleep(10);
    }

    [Fact]
    public void Assignar_configurar_i_treure_es_desa_al_perfil()
    {
        var (state, router, repository) = NewEngine();
        string path = Wav(1, "Sintonia");

        var cart = Send(router, new() { ["cmd"] = "cart.assign", ["page"] = 1, ["index"] = 5, ["path"] = path })["cart"]!;
        Assert.Equal(4, cart["pages"]!.GetValue<int>());
        Assert.Equal(4, cart["rows"]!.GetValue<int>());
        var button = cart["buttons"]![0]!;
        Assert.Equal((1, 5, "Sintonia"), (button["page"]!.GetValue<int>(), button["index"]!.GetValue<int>(), button["name"]!.GetValue<string>()));

        Send(router, new() { ["cmd"] = "cart.configure", ["page"] = 1, ["index"] = 5, ["name"] = " Falca ", ["color"] = "#d63384", ["gainDb"] = -3 });
        var saved = Assert.Single(repository.Load().Config.CartButtons);
        Assert.Equal(new CartButtonSettings(1, 5, path, "Falca", "#d63384", -3), saved);

        Send(router, new() { ["cmd"] = "cart.configure", ["page"] = 1, ["index"] = 5, ["color"] = "" }); // treure el color
        Assert.Null(repository.Load().Config.CartButtons.Single().Color);

        WaitReady(state, 1, 5);
        var got = Send(router, new() { ["cmd"] = "cart.get" })["cart"]!["buttons"]![0]!;
        Assert.Equal("ready", got["status"]!.GetValue<string>());
        Assert.Equal(1.0, got["duration"]!.GetValue<double>(), 2);

        Send(router, new() { ["cmd"] = "cart.clear", ["page"] = 1, ["index"] = 5 });
        Assert.Empty(repository.Load().Config.CartButtons);
        Assert.True(File.Exists(path)); // el fitxer no es toca
    }

    [Fact]
    public void Sortida_premer_i_aturar()
    {
        var (state, router, repository) = NewEngine();
        Send(router, new() { ["cmd"] = "cart.assign", ["page"] = 0, ["index"] = 0, ["path"] = Wav(3, "A") });
        WaitReady(state, 0, 0);
        Assert.Equal("bad_state", Error(router, new() { ["cmd"] = "cart.press", ["page"] = 0, ["index"] = 0 })); // sense sortida

        var cart = Send(router, new() { ["cmd"] = "cart.setDevice", ["deviceId"] = CartOut })["cart"]!;
        Assert.True(cart["outputReady"]!.GetValue<bool>());
        Assert.False(cart["sharesPlayerOutput"]!.GetValue<bool>());
        Assert.Equal(new DeviceRef(CartOut, "Botonera"), repository.Load().Config.CartDevice[EngineState.CartId]);

        var r = Send(router, new() { ["cmd"] = "cart.press", ["page"] = 0, ["index"] = 0 });
        Assert.True(r["playing"]!.GetValue<bool>());
        Assert.Single(r["cart"]!["playing"]!.AsArray());
        Assert.Single(CartJson.PositionEvent(state.Cart)["playing"]!.AsArray());
        Assert.False(Send(router, new() { ["cmd"] = "cart.press", ["page"] = 0, ["index"] = 0 })["playing"]!.GetValue<bool>());
        Send(router, new() { ["cmd"] = "cart.stop", ["page"] = 0, ["index"] = 0 });
        Send(router, new() { ["cmd"] = "cart.stopAll" });

        // La mateixa sortida que un reproductor: es permet i es diu.
        state.Players[0].SetDevice(AntennaOut);
        Assert.True(Send(router, new() { ["cmd"] = "cart.setDevice", ["deviceId"] = AntennaOut })["cart"]!["sharesPlayerOutput"]!.GetValue<bool>());
    }

    [Fact]
    public void Errors_de_parametres()
    {
        var (_, router, _) = NewEngine();
        string path = Wav(1, "B");
        Assert.Equal("bad_params", Error(router, new() { ["cmd"] = "cart.assign", ["page"] = 4, ["index"] = 0, ["path"] = path }));
        Assert.Equal("bad_params", Error(router, new() { ["cmd"] = "cart.assign", ["page"] = 0, ["index"] = 16, ["path"] = path }));
        Assert.Equal("bad_params", Error(router, new() { ["cmd"] = "cart.assign", ["page"] = 0, ["index"] = 1.5, ["path"] = path }));
        Assert.Equal("bad_params", Error(router, new() { ["cmd"] = "cart.assign", ["page"] = 0, ["index"] = 0, ["path"] = "relatiu.mp3" }));
        Assert.Equal("bad_params", Error(router, new() { ["cmd"] = "cart.setDevice", ["deviceId"] = "{0.0.1.00000000}.{micro}" })); // una entrada
        Send(router, new() { ["cmd"] = "cart.assign", ["page"] = 0, ["index"] = 0, ["path"] = path });
        Assert.Equal("bad_params", Error(router, new() { ["cmd"] = "cart.configure", ["page"] = 0, ["index"] = 0, ["color"] = "vermell" }));
        Assert.Equal("bad_params", Error(router, new() { ["cmd"] = "cart.configure", ["page"] = 0, ["index"] = 0, ["gainDb"] = 20 }));
        Assert.Equal("bad_params", Error(router, new() { ["cmd"] = "cart.configure", ["page"] = 0, ["index"] = 0, ["name"] = new string('x', 41) }));
        Assert.Equal("bad_state", Error(router, new() { ["cmd"] = "cart.configure", ["page"] = 0, ["index"] = 1, ["name"] = "buit" }));
    }

    [Fact]
    public void En_arrencar_es_torna_a_assignar_la_sortida_desada()
    {
        var repository = new ConfigRepository(Path.Combine(_folder, "config.json"));
        var saved = new EngineConfig();
        saved.CartDevice[EngineState.CartId] = new DeviceRef(CartOut, "Botonera");
        saved.CartButtons.Add(new CartButtonSettings(2, 3, Path.Combine(_folder, "x.mp3"), "X", "#112233", 1.5));
        repository.Save(saved);

        var loaded = repository.Load().Config;
        Assert.Equal(saved.CartButtons, loaded.CartButtons);

        var state = new EngineState([], [], cart: new CartWall(_ => new FakeOutput()));
        var report = new HardwareAssignments(state, repository).Start(Devices);
        Assert.Equal(CartOut, state.Cart.DeviceId);
        var check = Assert.Single(report.Elements);
        Assert.Equal("cart", HardwareJson.Report(report)["elements"]![0]!["element"]!.GetValue<string>());
        Assert.Equal("Botonera", HardwareAssignments.ElementName(check.Kind, check.ElementId));
    }

    [Fact]
    public void L_estat_i_el_mesurador_la_inclouen()
    {
        var (state, router, _) = NewEngine();
        Assert.NotNull(Send(router, new() { ["cmd"] = "state.get" })["cart"]);
        Assert.Equal(-90, MetersJson.Event([], [], cart: state.Cart)["cart"]![0]!.GetValue<double>());
    }
}
