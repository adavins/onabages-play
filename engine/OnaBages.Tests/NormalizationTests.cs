using System.Text.Json.Nodes;
using NAudio.Wave;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>
/// Normalització de sonoritat (fase 4, pas 5b; D-071): la mesura en segon pla (amb memòria cau),
/// el guany per pista (també en pistes encadenades), el límit de +10 dB, que no canvia la pista
/// que ja sona, i les ordres.
/// </summary>
public class NormalizationTests : IDisposable
{
    private const string DeviceId = "{0.0.0.00000000}.{proves}";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "normalitzacio-" + Guid.NewGuid().ToString("N"));
    private FakeOutput? _output;

    public NormalizationTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>Sinus de 1 kHz estèreo a <paramref name="dbfs"/> (de pic): la seva sonoritat és pràcticament la mateixa en LUFS.</summary>
    private string Tone(double dbfs, double seconds)
    {
        string path = Path.Combine(_folder, $"to-{dbfs}-{Guid.NewGuid():N}.wav");
        float amplitude = (float)Math.Pow(10, dbfs / 20);
        int frames = (int)(48000 * seconds);
        var samples = new float[frames * 2];
        for (int i = 0; i < frames; i++)
            samples[i * 2] = samples[i * 2 + 1] = amplitude * (float)Math.Sin(2 * Math.PI * 1000 * i / 48000);
        using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
            writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private Player NewPlayer(ILoudnessCache? cache = null)
    {
        var player = new Player("1", _ => _output = new FakeOutput(), new TrackAnalyzer(synchronous: true, cache: cache));
        player.SetDevice(DeviceId);
        return player;
    }

    /// <summary>Fa de fil d'àudio fins que el reproductor s'atura. Retorna el canal esquerre.</summary>
    private List<float> PumpUntilStopped(Player player)
    {
        Thread.Sleep(150);
        var left = new List<float>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (player.State == PlayerState.Playing && DateTime.UtcNow < deadline)
        {
            var block = _output!.Pump(480);
            for (int i = 0; i < block.Length; i += 2) left.Add(block[i]);
            player.Tick();
            Thread.Sleep(1);
        }
        return left;
    }

    private static double PeakDb(IEnumerable<float> samples) => 20 * Math.Log10(samples.Max(Math.Abs));

    // --- Mesura i memòria cau ---

    [Fact]
    public void En_afegir_una_pista_se_nomes_mesura_la_sonoritat()
    {
        using var player = NewPlayer();

        var id = player.Playlist.Add([Tone(-20, 3)])[0];

        Assert.InRange(player.Playlist.Find(id)!.LoudnessLufs!.Value, -20.2, -19.8);
    }

    private sealed class CountingCache : ILoudnessCache
    {
        public readonly Dictionary<string, TrackLoudness> Saved = [];
        public int Writes;
        public TrackLoudness? Read(string path) => Saved.GetValueOrDefault(path);
        public void Write(string path, TrackLoudness loudness) { Saved[path] = loudness; Writes++; }
    }

    [Fact]
    public void Una_pista_que_ja_sha_mesurat_surt_de_la_memoria_cau()
    {
        var cache = new CountingCache();
        using var player = NewPlayer(cache);
        string tone = Tone(-15, 2);

        player.Playlist.Add([tone]);
        var second = player.Playlist.Add([tone])[0];

        Assert.Equal(1, cache.Writes); // només s'ha mesurat la primera vegada
        Assert.InRange(player.Playlist.Find(second)!.LoudnessLufs!.Value, -15.2, -14.8);
    }

    [Fact]
    public void La_memoria_cau_en_fitxers_desa_i_llegeix_i_detecta_si_el_fitxer_canvia()
    {
        var cache = new LoudnessCacheFile(Path.Combine(_folder, "cache"));
        string tone = Tone(-10, 1);
        string silence = Tone(-200, 1);

        cache.Write(tone, new TrackLoudness(-10.04, 0.316f));
        cache.Write(silence, new TrackLoudness(null, 0f));

        Assert.Equal(new TrackLoudness(-10.04, 0.316f), cache.Read(tone));
        Assert.Equal(new TrackLoudness(null, 0f), cache.Read(silence));
        File.SetLastWriteTimeUtc(tone, DateTime.UtcNow.AddMinutes(5)); // el fitxer ha canviat
        Assert.Null(cache.Read(tone));
    }

    // --- Guany ---

    [Fact]
    public void Desactivada_la_pista_sona_igual()
    {
        using var player = NewPlayer();
        player.Playlist.Add([Tone(-20, 1)]);

        player.Play();
        var output = PumpUntilStopped(player);

        Assert.InRange(PeakDb(output), -20.1, -19.9);
        Assert.Null(player.NormalizationDb);
    }

    [Fact]
    public void Activada_la_pista_arriba_a_lobjectiu()
    {
        using var player = NewPlayer();
        player.Playlist.Add([Tone(-20, 1)]);
        player.SetNormalization(true, -12);

        player.Play();
        double? applied = player.NormalizationDb;
        var output = PumpUntilStopped(player);

        Assert.InRange(applied!.Value, 7.8, 8.2);         // de −20 a −12: +8 dB
        Assert.InRange(PeakDb(output), -12.2, -11.8);
    }

    [Fact]
    public void Una_pista_molt_fluixa_puja_com_a_molt_10_dB()
    {
        using var player = NewPlayer();
        player.Playlist.Add([Tone(-40, 1)]);
        player.SetNormalization(true, -12);

        player.Play();
        var output = PumpUntilStopped(player);

        Assert.Equal(Player.MaxNormalizationBoostDb, player.NormalizationDb ?? 10, 0.01); // (es posa a null en acabar)
        Assert.InRange(PeakDb(output), -30.2, -29.8);       // −40 + 10
    }

    [Fact]
    public void Activar_la_mentre_sona_no_canvia_la_pista_que_sona()
    {
        using var player = NewPlayer();
        player.Playlist.Add([Tone(-20, 1)]);
        player.Play();

        player.SetNormalization(true, -12);
        var output = PumpUntilStopped(player);

        Assert.InRange(PeakDb(output), -20.1, -19.9);
    }

    [Fact]
    public void Pistes_encadenades_de_nivell_diferent_surten_igualades()
    {
        using var player = NewPlayer();
        var ids = player.Playlist.Add([Tone(-20, 1), Tone(-6, 1)]);
        player.Playlist.SetFlags(ids[0], autoNext: true);
        player.SetNormalization(true, -12);

        player.Play();
        var output = PumpUntilStopped(player);

        // Abans: 14 dB de diferència (−20 i −6). Després, totes dues a −12 dBFS.
        Assert.Equal(ids[1], player.LastPlayedId);
        Assert.InRange(PeakDb(output.Take(24000)), -12.2, -11.8);
        Assert.InRange(PeakDb(output.Skip(48000 + 4800).Take(24000)), -12.2, -11.8);
    }

    // --- Ordres ---

    private (CommandRouter Router, ConfigRepository Repository, Player Player) NewRouter()
    {
        var repository = new ConfigRepository(Path.Combine(_folder, "dades", "config.json"));
        var store = new ConfigStore(repository);
        store.Load();
        var player = NewPlayer();
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
    public void Activar_la_normalitzacio_dun_reproductor_es_desa_al_perfil()
    {
        var (router, repository, player) = NewRouter();

        var r = Ok(router, """{ "v": 1, "id": 1, "cmd": "player.setNormalization", "player": "1", "enabled": true }""");

        Assert.True(r["player"]!["normalization"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(-12, r["player"]!["normalization"]!["targetLufs"]!.GetValue<double>());
        Assert.True(player.Normalize);
        Assert.Contains("1", repository.Load().Config.NormalizedPlayers);

        Ok(router, """{ "v": 1, "id": 2, "cmd": "player.setNormalization", "player": "1", "enabled": false }""");
        Assert.Empty(repository.Load().Config.NormalizedPlayers);
    }

    [Fact]
    public void Lobjectiu_es_canvia_a_settings_i_arriba_als_reproductors()
    {
        var (router, repository, player) = NewRouter();

        var r = Ok(router, """{ "v": 1, "id": 1, "cmd": "settings.set", "loudness": { "targetLufs": -14 } }""");

        Assert.Equal(-14, r["settings"]!["loudness"]!["targetLufs"]!.GetValue<double>());
        Assert.Equal(-14, player.NormalizationTargetLufs);
        Assert.Equal(-14, repository.Load().Config.LoudnessTargetLufs);
    }

    [Theory]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "settings.set", "loudness": { "targetLufs": -40 } }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "player.setNormalization", "player": "1" }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "player.setNormalization", "player": "1", "enabled": "sí" }""")]
    public void Valors_no_valids_donen_bad_params(string json)
    {
        var (router, _, _) = NewRouter();

        Assert.Equal("bad_params", router.Handle(json)["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public void La_llista_diu_la_sonoritat_de_cada_pista()
    {
        var (router, _, player) = NewRouter();
        player.Playlist.Add([Tone(-18, 2)]);

        var item = Ok(router, """{ "v": 1, "id": 1, "cmd": "playlist.get", "player": "1" }""")["playlist"]!["items"]![0]!;

        Assert.InRange(item["loudness"]!.GetValue<double>(), -18.2, -17.8);
    }
}
