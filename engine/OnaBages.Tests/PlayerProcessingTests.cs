using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>
/// Compressor/limitador dins dels reproductors (fase 4, pas 4b; D-067): s'aplica al so que surt,
/// abans del fader, es pot canviar mentre sona, es desa al perfil i l'indicador de reducció arriba
/// amb els mesuradors.
/// </summary>
public class PlayerProcessingTests : IDisposable
{
    private const string DeviceId = "{0.0.0.00000000}.{proves}";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "processament-" + Guid.NewGuid().ToString("N"));
    private FakeOutput? _output;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>Reproductor sonant un to de 440 Hz: esquerra −20 dBFS (0,1), dreta −14 dBFS (0,2).</summary>
    private Player PlayingPlayer(double seconds = 3)
    {
        var player = new Player("1", _ => _output = new FakeOutput());
        player.SetDevice(DeviceId);
        player.Load(TestAudio.CreateWav(48000, 2, seconds));
        player.Play();
        Thread.Sleep(200); // que el fil de lectura ompli la memòria
        return player;
    }

    /// <summary>Pic de cada canal de <paramref name="ms"/> mil·lisegons de so.</summary>
    private (float Left, float Right) PumpPeaks(int ms)
    {
        float left = 0, right = 0;
        for (int i = 0; i < ms / 10; i++)
        {
            var samples = _output!.Pump(480);
            for (int s = 0; s < samples.Length; s += 2)
            {
                left = Math.Max(left, Math.Abs(samples[s]));
                right = Math.Max(right, Math.Abs(samples[s + 1]));
            }
        }
        return (left, right);
    }

    private static float Gain(double db) => (float)Math.Pow(10, db / 20);

    // --- Al so ---

    [Fact]
    public void Desactivat_el_so_surt_igual()
    {
        using var player = PlayingPlayer();

        var (left, right) = PumpPeaks(500);

        Assert.Equal(0.1f, left, 0.005f);
        Assert.Equal(0.2f, right, 0.005f);
    }

    [Fact]
    public void Amb_el_SqrSoft_el_so_surt_mes_fort()
    {
        using var player = PlayingPlayer();
        player.SetDynamics(DynamicsSettings.SqrSoftLaCentral);

        PumpPeaks(200); // els guanys hi arriben en uns 20 ms
        var (left, right) = PumpPeaks(500);

        // +9,5 dB, sense arribar al llindar (−2,9 dBFS): esquerra −10,5 dBFS, dreta −4,5 dBFS.
        Assert.Equal(0.1f * Gain(9.5), left, 0.01f);
        Assert.Equal(0.2f * Gain(9.5), right, 0.01f);
    }

    [Fact]
    public void El_fader_va_despres_del_compressor()
    {
        using var player = PlayingPlayer();
        player.SetDynamics(DynamicsSettings.SqrSoftLaCentral);
        player.SetVolume(-6);

        PumpPeaks(200);
        var (left, _) = PumpPeaks(500);

        Assert.Equal(0.1f * Gain(9.5) * Gain(-6), left, 0.01f);
    }

    [Fact]
    public void Es_conserva_dun_a_pista_a_laltra()
    {
        var player = new Player("1", _ => _output = new FakeOutput());
        player.SetDynamics(DynamicsSettings.SqrSoftLaCentral);
        player.SetDevice(DeviceId);
        player.Load(TestAudio.CreateWav(48000, 2, 2));
        player.Play();
        Thread.Sleep(200);

        PumpPeaks(200);
        var (left, _) = PumpPeaks(300);

        Assert.Equal(0.1f * Gain(9.5), left, 0.01f);
        player.Dispose();
    }

    [Fact]
    public void Lindicador_de_reduccio_arriba_amb_els_mesuradors()
    {
        using var player = PlayingPlayer();
        // Llindar baix: el to de −20/−14 dBFS es comprimeix.
        player.SetDynamics(new DynamicsSettings(true, ThresholdDb: -30, Ratio: 10, AttackMs: 1, ReleaseMs: 100));
        PumpPeaks(300);

        var ev = MetersJson.Event([player], []);

        double reduction = ev["playerReduction"]!["1"]!.GetValue<double>();
        Assert.InRange(reduction, 10, 20); // dreta: (−14 − (−30)) × 0,9 = 14,4 dB
    }

    // --- Ordres ---

    private (CommandRouter Router, ConfigRepository Repository, Player Player) NewRouter()
    {
        Directory.CreateDirectory(_folder);
        var repository = new ConfigRepository(Path.Combine(_folder, "config.json"));
        var store = new ConfigStore(repository);
        store.Load();
        var player = new Player("1", _ => new FakeOutput());
        var router = new CommandRouter(new EngineState([player], []), () => { }, () => [], config: store);
        return (router, repository, player);
    }

    private static JsonObject Ok(CommandRouter router, string json)
    {
        var r = router.Handle(json);
        Assert.True(r["ok"]!.GetValue<bool>(), r.ToJsonString());
        return r["result"]!.AsObject();
    }

    [Fact]
    public void Per_defecte_el_reproductor_porta_el_compressor_desactivat()
    {
        var (router, _, _) = NewRouter();

        var player = Ok(router, """{ "v": 1, "id": 1, "cmd": "state.get" }""")["players"]![0]!;

        Assert.False(player["processing"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void Un_preset_saplica_i_es_desa_al_perfil()
    {
        var (router, repository, player) = NewRouter();

        var r = Ok(router, """{ "v": 1, "id": 1, "cmd": "player.setProcessing", "player": "1", "preset": "sqrsoft-la-central" }""");

        Assert.Equal(DynamicsSettings.SqrSoftLaCentral, player.Dynamics);
        Assert.Equal(9.5, r["player"]!["processing"]!["inputGainDb"]!.GetValue<double>());
        Assert.Equal(DynamicsSettings.SqrSoftLaCentral, repository.Load().Config.PlayerDynamics["1"]);
    }

    [Fact]
    public void Els_camps_que_no_senvien_es_conserven_i_els_de_fora_de_marge_van_al_limit()
    {
        var (router, _, player) = NewRouter();
        Ok(router, """{ "v": 1, "id": 1, "cmd": "player.setProcessing", "player": "1", "preset": "sqrsoft-la-central" }""");

        Ok(router, """{ "v": 1, "id": 2, "cmd": "player.setProcessing", "player": "1", "processing": { "ratio": 4, "inputGainDb": 99 } }""");

        Assert.Equal(4, player.Dynamics.Ratio);
        Assert.Equal(DynamicsSettings.MaxGainDb, player.Dynamics.InputGainDb);
        Assert.Equal(-2.9, player.Dynamics.ThresholdDb); // es conserva
        Assert.True(player.Dynamics.Enabled);
    }

    [Theory]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "player.setProcessing", "player": "1" }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "player.setProcessing", "player": "1", "preset": "no-existeix" }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "player.setProcessing", "player": "9", "preset": "suau" }""")]
    public void Ordres_no_valides_donen_bad_params(string json)
    {
        var (router, _, _) = NewRouter();

        Assert.Equal("bad_params", router.Handle(json)["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public void Els_ajustos_tornen_a_carregar_se_amb_el_perfil()
    {
        var (router, repository, _) = NewRouter();
        Ok(router, """{ "v": 1, "id": 1, "cmd": "player.setProcessing", "player": "1", "processing": { "enabled": true, "thresholdDb": -6, "stereoLink": true } }""");

        var saved = repository.Load().Config.PlayerDynamics["1"];

        Assert.True(saved.Enabled);
        Assert.Equal(-6, saved.ThresholdDb);
        Assert.True(saved.StereoLink);
    }

    [Fact]
    public void Els_ajustos_diuen_els_presets()
    {
        var (router, _, _) = NewRouter();

        var presets = Ok(router, """{ "v": 1, "id": 1, "cmd": "settings.get" }""")["settings"]!["processingPresets"]!.AsArray();

        Assert.Contains(presets, p => p!["id"]!.GetValue<string>() == "sqrsoft-la-central"
                                      && p["processing"]!["releaseMs"]!.GetValue<double>() == 1450);
    }
}
