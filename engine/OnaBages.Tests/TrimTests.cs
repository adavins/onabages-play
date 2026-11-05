using System.Text.Json.Nodes;
using NAudio.Wave;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>
/// Retall automàtic de silencis als reproductors (fase 5, pas 8b; D-080). Pistes amb silenci al principi
/// i al final i so d'un valor fix al mig: així es compta exactament quantes mostres de so surten (totes)
/// i on comencen (just després del marge).
/// </summary>
public class TrimTests : IDisposable
{
    private const string DeviceId = "{0.0.0.00000000}.{proves}";
    private const int Rate = 48000;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "retall-" + Guid.NewGuid().ToString("N"));
    private FakeOutput? _output;

    public TrimTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>Silenci, <paramref name="seconds"/> de so a <paramref name="value"/> i silenci.</summary>
    private string Track(float value, double silenceBefore, double seconds, double silenceAfter, int rate = Rate)
    {
        string path = Path.Combine(_folder, $"t-{Guid.NewGuid():N}.wav");
        int before = (int)Math.Round(silenceBefore * rate), sound = (int)Math.Round(seconds * rate), after = (int)Math.Round(silenceAfter * rate);
        var samples = new float[(before + sound + after) * 2];
        for (int f = before; f < before + sound; f++) samples[f * 2] = samples[f * 2 + 1] = value;
        using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, 2)))
            writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private Player NewPlayer(bool trim, int leadMs = 10, int tailMs = 50, int crossfadeMs = 0)
    {
        var player = new Player("1", _ => _output = new FakeOutput(), new TrackAnalyzer(synchronous: true));
        player.SetDevice(DeviceId);
        player.SetTrim(new Player.TrimSettings(trim, -60, leadMs, tailMs));
        player.SetCrossfade(crossfadeMs);
        return player;
    }

    /// <summary>Fa de fil d'àudio fins que s'atura. Retorna tot el canal esquerre (amb els silencis).</summary>
    private List<float> PumpUntilStopped(Player player)
    {
        Thread.Sleep(150);
        var left = new List<float>();
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (player.State == PlayerState.Playing && DateTime.UtcNow < deadline)
        {
            var block = _output!.Pump(480);
            for (int i = 0; i < block.Length; i += 2) left.Add(block[i]);
            player.Tick();
            Thread.Sleep(1);
        }
        return left;
    }

    private static int CountOf(List<float> output, float value) => output.Count(v => Math.Abs(v - value) < 1e-4);

    [Fact]
    public void Desactivat_sona_la_pista_sencera()
    {
        var player = NewPlayer(trim: false);
        player.Playlist.Add([Track(0.4f, 0.5, 1, 0.5)]);
        player.Play();

        var output = PumpUntilStopped(player);

        Assert.Equal((int)(0.5 * Rate), output.FindIndex(v => v != 0)); // el silenci del principi hi és
        Assert.Equal(Rate, CountOf(output, 0.4f));
        player.Dispose();
    }

    [Fact]
    public void Activat_comenca_just_abans_del_so_i_no_se_nen_perd_cap_mostra()
    {
        var player = NewPlayer(trim: true, leadMs: 10, tailMs: 50);
        player.Playlist.Add([Track(0.4f, 0.5, 1, 0.5)]);
        player.Play();
        var position = player.Position;
        var duration = player.Duration;

        var output = PumpUntilStopped(player);

        Assert.Equal(480, output.FindIndex(v => v != 0));       // 10 ms de marge i el so
        Assert.Equal(Rate, CountOf(output, 0.4f));              // tot el so (1 s), cap mostra menys
        Assert.Equal(0.49, position.TotalSeconds, 0.001);       // la posició és la del fitxer
        Assert.Equal(1.55, duration.TotalSeconds, 0.001);       // s'acaba a l'últim so + 50 ms
        player.Dispose();
    }

    [Fact]
    public void Dues_pistes_encadenades_queden_separades_només_pels_marges()
    {
        var player = NewPlayer(trim: true, leadMs: 10, tailMs: 50);
        var ids = player.Playlist.Add([Track(0.4f, 0.5, 1, 0.5), Track(0.2f, 0.3, 1, 0.3)]);
        player.Playlist.SetFlags(ids[0], autoNext: true);
        player.Play();

        var output = PumpUntilStopped(player);

        int endA = output.FindLastIndex(v => Math.Abs(v - 0.4f) < 1e-4);
        int startB = output.FindIndex(v => Math.Abs(v - 0.2f) < 1e-4);
        Assert.Equal((50 + 10) * Rate / 1000, startB - endA - 1); // 60 ms entre el so d'A i el de B
        Assert.Equal(Rate, CountOf(output, 0.4f));
        Assert.Equal(Rate, CountOf(output, 0.2f));
        player.Dispose();
    }

    [Fact]
    public void Amb_fos_les_pistes_es_fonen_a_partir_del_final_del_so()
    {
        // Fos de 60 ms: exactament la cua d'A (50 ms) més el marge de B (10 ms): el so d'A s'acaba quan comença el de B.
        var player = NewPlayer(trim: true, leadMs: 10, tailMs: 50, crossfadeMs: 60);
        var ids = player.Playlist.Add([Track(0.4f, 0.5, 1, 0.5), Track(0.2f, 0.3, 1, 0.3)]);
        player.Playlist.SetFlags(ids[0], autoNext: true);
        player.Play();

        var output = PumpUntilStopped(player);
        int first = output.FindIndex(v => v != 0);
        int last = output.FindLastIndex(v => v != 0);

        Assert.Equal(1.0 + 1.0, (last - first + 1) / (double)Rate, 0.002); // tot el so, sense forats ni sobreposició de so
        Assert.All(output.Skip(first).Take(last - first), v => Assert.True(v > 0.001f, "cap forat"));
        player.Dispose();
    }

    [Fact]
    public void Una_pista_a_44_1_kHz_tambe_comenca_on_toca()
    {
        var player = NewPlayer(trim: true, leadMs: 10, tailMs: 50);
        player.Playlist.Add([Track(0.4f, 0.5, 1, 0.5, rate: 44100)]);
        player.Play();

        var output = PumpUntilStopped(player);

        // Remostrejat a 48 kHz: el filtre del remostrejador escampa uns pocs mostres a la vora.
        Assert.InRange(output.FindIndex(v => Math.Abs(v) > 0.001f), 470, 490);
        player.Dispose();
    }

    [Fact]
    public void Un_salt_a_mig_so_es_respecta_i_para_igualment_al_final_del_so()
    {
        var player = NewPlayer(trim: true);
        var id = player.Playlist.Add([Track(0.4f, 0.5, 1, 0.5)])[0];

        player.PlayItem(id, TimeSpan.FromSeconds(1.0));
        var duration = player.Duration;
        var output = PumpUntilStopped(player);

        Assert.Equal(0, output.FindIndex(v => v != 0));          // comença a 1,0 s, ja dins del so
        Assert.Equal(Rate / 2, CountOf(output, 0.4f));            // de 1,0 a 1,5 s
        Assert.Equal(1.55, duration.TotalSeconds, 0.001);
        player.Dispose();
    }

    [Fact]
    public void Una_pista_tota_silenci_sona_sencera()
    {
        var player = NewPlayer(trim: true);
        player.Playlist.Add([Track(0f, 0.5, 0, 0.5)]);
        player.Play();
        var duration = player.Duration;

        PumpUntilStopped(player);

        Assert.Equal(1.0, duration.TotalSeconds, 0.001);
        player.Dispose();
    }

    // --- Ordres ---

    [Fact]
    public void Ordres_i_perfil()
    {
        var repository = new ConfigRepository(Path.Combine(_folder, "dades", "config.json"));
        var store = new ConfigStore(repository);
        store.Load();
        var player = NewPlayer(trim: false);
        var router = new CommandRouter(new EngineState([player], []), () => { }, () => [], config: store);

        var r = router.Handle("""{ "v": 1, "id": 1, "cmd": "player.setTrim", "player": "1", "enabled": true }""");
        Assert.True(r["result"]!["player"]!["trim"]!["enabled"]!.GetValue<bool>());
        Assert.Contains("1", repository.Load().Config.TrimPlayers);

        r = router.Handle("""{ "v": 1, "id": 2, "cmd": "settings.set", "trim": { "thresholdDb": -50, "leadMs": 25, "tailMs": 200 } }""");
        var trim = r["result"]!["settings"]!["trim"]!;
        Assert.Equal(-50, trim["thresholdDb"]!.GetValue<int>());
        Assert.Equal(new Player.TrimSettings(true, -50, 25, 200), player.Trim);
        Assert.Equal(200, repository.Load().Config.TrimTailMs);

        Assert.Equal("bad_params", router.Handle("""{ "v": 1, "id": 3, "cmd": "settings.set", "trim": { "thresholdDb": -55 } }""")["error"]!["code"]!.GetValue<string>());
        Assert.Equal("bad_params", router.Handle("""{ "v": 1, "id": 4, "cmd": "settings.set", "trim": { "leadMs": 5000 } }""")["error"]!["code"]!.GetValue<string>());

        player.Playlist.Add([Track(0.4f, 0.5, 1, 0.5)]);
        var item = router.Handle("""{ "v": 1, "id": 5, "cmd": "playlist.get", "player": "1" }""")["result"]!["playlist"]!["items"]![0]!;
        var sound60 = item["sound"]!.AsArray().First(s => s![0]!.GetValue<int>() == -60)!;
        Assert.Equal(0.5, sound60[1]!.GetValue<double>(), 0.0001);
        Assert.Equal(1.5, sound60[2]!.GetValue<double>(), 0.0001);
        player.Dispose();
    }
}
