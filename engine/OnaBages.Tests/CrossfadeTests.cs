using System.Text.Json.Nodes;
using NAudio.Wave;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>
/// Encadenament amb fos (fase 5; D-077). Es fan servir pistes d'un valor constant (A = 0,4 i B = 0,2):
/// així es veu exactament on comença i on acaba el fos, quant dura i quins valors té.
/// </summary>
public class CrossfadeTests : IDisposable
{
    private const string DeviceId = "{0.0.0.00000000}.{proves}";
    private const int Rate = 48000;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "encadenament-" + Guid.NewGuid().ToString("N"));
    private FakeOutput? _output;

    public CrossfadeTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>Pista estèreo d'un valor constant.</summary>
    private string Constant(float value, double seconds)
    {
        string path = Path.Combine(_folder, $"c-{value}-{Guid.NewGuid():N}.wav");
        var samples = Enumerable.Repeat(value, (int)(Rate * seconds) * 2).ToArray();
        using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2)))
            writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private Player NewPlayer(int crossfadeMs)
    {
        var player = new Player("1", _ => _output = new FakeOutput(), new TrackAnalyzer(synchronous: true));
        player.SetDevice(DeviceId);
        player.SetCrossfade(crossfadeMs);
        return player;
    }

    /// <summary>Fa de fil d'àudio fins que s'atura. Retorna el canal esquerre, sense el silenci del final.</summary>
    private List<float> PumpUntilStopped(Player player, double maxSeconds = 40) // de sobres, també amb l'ordinador carregat
    {
        Thread.Sleep(150);
        var left = new List<float>();
        var deadline = DateTime.UtcNow.AddSeconds(maxSeconds);
        while (player.State == PlayerState.Playing && DateTime.UtcNow < deadline)
        {
            var block = _output!.Pump(480);
            for (int i = 0; i < block.Length; i += 2) left.Add(block[i]);
            player.Tick();
            Thread.Sleep(1);
        }
        int last = left.FindLastIndex(v => Math.Abs(v) > 0.001f);
        return left.Take(last + 1).ToList();
    }

    private (Player Player, List<float> Output) PlayAB(int crossfadeMs, double secondsA = 1, double secondsB = 1)
    {
        var player = NewPlayer(crossfadeMs);
        var ids = player.Playlist.Add([Constant(0.4f, secondsA), Constant(0.2f, secondsB)]);
        player.Playlist.SetFlags(ids[0], autoNext: true);
        player.Play();
        return (player, PumpUntilStopped(player));
    }

    [Fact]
    public void Per_defecte_no_hi_ha_fos()
    {
        var (player, output) = PlayAB(0);

        Assert.Equal(0, player.CrossfadeMs);
        Assert.Equal(2 * Rate, output.Count);                     // tot sencer, sense silenci ni sobreposició
        Assert.All(output, v => Assert.True(Math.Abs(v - 0.4f) < 1e-4 || Math.Abs(v - 0.2f) < 1e-4));
        player.Dispose();
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(2000)]
    public void Amb_fos_les_pistes_se_sobreposen_el_temps_triat(int ms)
    {
        var (player, output) = PlayAB(ms, secondsA: 3, secondsB: 3);
        int overlap = ms * Rate / 1000;

        Assert.Equal(6 * Rate - overlap, output.Count);           // s'escurça exactament el fos
        int start = 3 * Rate - overlap;
        Assert.Equal(0.4f, output[start - 1], 1e-4f);              // just abans: només A
        Assert.Equal(0.2f, output[3 * Rate], 1e-4f);               // just després: només B
        // A la meitat, igual potència: 0,4·cos(45°) + 0,2·sin(45°) ≈ 0,424.
        Assert.Equal(0.424f, output[start + overlap / 2], 0.005f);
        player.Dispose();
    }

    [Fact]
    public void Si_no_ve_cap_pista_la_que_sona_acaba_sencera()
    {
        var player = NewPlayer(500);
        player.Playlist.Add([Constant(0.4f, 1), Constant(0.2f, 1)]); // sense avanç automàtic
        player.Play();

        var output = PumpUntilStopped(player);

        Assert.Equal(Rate, output.Count);
        Assert.Equal(0.4f, output[^1], 1e-4f); // sense fos al final
        player.Dispose();
    }

    [Fact]
    public void Un_bucle_no_fa_fos()
    {
        var player = NewPlayer(200);
        var ids = player.Playlist.Add([Constant(0.4f, 0.5)]);
        player.Playlist.SetFlags(ids[0], loop: true);
        player.Play();
        Thread.Sleep(150);

        var left = new List<float>();
        for (int i = 0; i < 300; i++) // 1,5 s: tres voltes
        {
            var block = _output!.Pump(240);
            for (int s = 0; s < block.Length; s += 2) left.Add(block[s]);
            player.Tick();
            Thread.Sleep(1);
        }

        Assert.All(left, v => Assert.Equal(0.4f, v, 1e-4f));
        player.Dispose();
    }

    [Fact]
    public void Si_la_pista_seguent_es_mes_curta_que_el_fos_tota_se_sobreposa()
    {
        var (player, output) = PlayAB(500, secondsA: 1, secondsB: 0.2);

        Assert.Equal(Rate, output.Count);                          // B (0,2 s) cap dins del final d'A
        Assert.Equal(0.4f, output[Rate - (int)(0.2 * Rate) - 1], 1e-4f);
        player.Dispose();
    }

    [Fact]
    public void La_pista_seguent_passa_a_ser_lactual_quan_comenca_el_fos()
    {
        var player = NewPlayer(500);
        var ids = player.Playlist.Add([Constant(0.4f, 1), Constant(0.2f, 1)]);
        player.Playlist.SetFlags(ids[0], autoNext: true);
        player.Play();
        Thread.Sleep(150);

        // 0,6 s de so: ja som dins del fos (comença a 0,5 s).
        for (int i = 0; i < 60; i++) { _output!.Pump(480); player.Tick(); Thread.Sleep(1); }

        Assert.Equal(ids[1], player.CurrentItemId);
        Assert.Equal(ids[0], player.LastPlayedId);
        player.Dispose();
    }

    // --- Canvi de pista a mà amb fos (D-078) ---

    /// <summary>Fa de fil d'àudio uns quants blocs. Retorna el canal esquerre.</summary>
    private List<float> Pump(Player player, int blocks, int channels = 2)
    {
        var left = new List<float>();
        for (int b = 0; b < blocks; b++)
        {
            var block = _output!.Pump(480);
            for (int i = 0; i < block.Length; i += channels) left.Add(block[i]);
            player.Tick();
            Thread.Sleep(1);
        }
        return left;
    }

    [Fact]
    public void Fer_sonar_una_pista_a_ma_amb_fos_no_fa_cap_tall()
    {
        var player = NewPlayer(200);
        var ids = player.Playlist.Add([Constant(0.4f, 3), Constant(0.2f, 1)]);
        player.Play();
        Thread.Sleep(150);
        var before = Pump(player, 50); // 0,5 s d'A

        player.PlayItem(ids[1]);
        Assert.Equal(ids[1], player.CurrentItemId);               // la nova ja és l'actual
        Assert.Equal(ids[0], player.LastPlayedId);                // i la d'abans, l'última reproduïda
        Assert.True(player.Position < TimeSpan.FromSeconds(0.1));
        var after = PumpUntilStopped(player);

        Assert.All(before, v => Assert.Equal(0.4f, v, 1e-4f));
        // Fos de 200 ms (9.600 mostres): comença amb A, a la meitat la barreja d'igual potència, i després B.
        Assert.Equal(0.4f, after[0], 0.01f);
        Assert.Equal(0.424f, after[4800], 0.01f);
        Assert.Equal(0.2f, after[9600 + 10], 1e-4f);
        Assert.True(after.Min() > 0.19f, "cap forat de silenci durant el canvi");
        Assert.Equal(Rate, after.Count);                          // B sencera (1 s), sobreposada al final d'A
        player.Dispose();
    }

    [Fact]
    public void Si_la_pista_nova_no_es_pot_obrir_la_que_sona_continua()
    {
        var player = NewPlayer(200);
        string broken = Path.Combine(_folder, "malmesa.mp3");
        File.WriteAllText(broken, "no és un MP3");
        var ids = player.Playlist.Add([Constant(0.4f, 3)]);
        var brokenId = player.Playlist.Add([broken])[0];
        player.Play();
        Thread.Sleep(150);
        Pump(player, 20);

        Assert.Throws<PlayerCommandException>(() => player.PlayItem(brokenId));

        Assert.Equal(PlayerState.Playing, player.State);
        Assert.Equal(ids[0], player.CurrentItemId);
        Assert.All(Pump(player, 50), v => Assert.Equal(0.4f, v, 1e-4f)); // continua sense cap tall
        player.Dispose();
    }

    [Fact]
    public void Sense_fos_fer_sonar_una_pista_a_ma_va_com_sempre()
    {
        var player = NewPlayer(0);
        var ids = player.Playlist.Add([Constant(0.4f, 3), Constant(0.2f, 1)]);
        player.Play();
        Thread.Sleep(150);
        Pump(player, 50);

        player.PlayItem(ids[1]);
        var after = PumpUntilStopped(player);

        Assert.All(after, v => Assert.Equal(0.2f, v, 1e-4f)); // directament B, sense barreja
        player.Dispose();
    }

    [Fact]
    public void Amb_una_sortida_de_6_canals_tambe_fa_el_fos()
    {
        var player = new Player("1", _ => _output = new FakeOutput(48000, 6), new TrackAnalyzer(synchronous: true));
        player.SetDevice(DeviceId);
        player.SetCrossfade(200);
        var ids = player.Playlist.Add([Constant(0.4f, 3), Constant(0.2f, 1)]);
        player.Play();
        Thread.Sleep(150);
        Pump(player, 20, channels: 6);

        player.PlayItem(ids[1]);
        Thread.Sleep(150);
        var after = Pump(player, 40, channels: 6);

        Assert.Equal(0.424f, after[4800], 0.01f);
        Assert.Equal(0.2f, after[^1], 1e-4f);
        player.Dispose();
    }

    [Fact]
    public void El_fil_daudio_no_crea_objectes_durant_el_fos()
    {
        var a = new SampleRingBuffer(Rate * 2);
        var b = new SampleRingBuffer(Rate * 2);
        a.Write(Enumerable.Repeat(0.4f, Rate * 2).ToArray());
        b.Write(Enumerable.Repeat(0.2f, Rate * 2).ToArray());
        var stream = new PlayerStream(a, Rate, 2);
        var buffer = new byte[480 * 2 * sizeof(float)];
        stream.Read(buffer); // escalfar
        stream.SwitchTo(b, 9600);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 30; i++) stream.Read(buffer); // tot el fos (200 ms) i una mica més
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }

    // --- Ordres ---

    [Fact]
    public void Lordre_el_canvia_i_es_desa_al_perfil()
    {
        var repository = new ConfigRepository(Path.Combine(_folder, "dades", "config.json"));
        var store = new ConfigStore(repository);
        store.Load();
        var player = NewPlayer(0);
        var router = new CommandRouter(new EngineState([player], []), () => { }, () => [], config: store);

        var r = router.Handle("""{ "v": 1, "id": 1, "cmd": "player.setCrossfade", "player": "1", "ms": 100 }""");

        Assert.Equal(100, r["result"]!["player"]!["crossfadeMs"]!.GetValue<int>());
        Assert.Equal(100, repository.Load().Config.PlayerCrossfadeMs["1"]);
        router.Handle("""{ "v": 1, "id": 2, "cmd": "player.setCrossfade", "player": "1", "ms": 0 }""");
        Assert.Empty(repository.Load().Config.PlayerCrossfadeMs);

        Assert.Equal("bad_params", router.Handle("""{ "v": 1, "id": 3, "cmd": "player.setCrossfade", "player": "1", "ms": 20000 }""")["error"]!["code"]!.GetValue<string>());
        Assert.Equal("bad_params", router.Handle("""{ "v": 1, "id": 4, "cmd": "player.setCrossfade", "player": "1", "ms": -5 }""")["error"]!["code"]!.GetValue<string>());
        player.Dispose();
    }
}
