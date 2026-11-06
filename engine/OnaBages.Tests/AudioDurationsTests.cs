using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>
/// Durada dels àudios de l'explorador (fase 5, pas 15a; D-104): explorer.list dona les que ja se saben i les altres
/// es mesuren en un fil a part i arriben a grups (Measured → avís explorer.durations).
/// </summary>
public class AudioDurationsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "durades-" + Guid.NewGuid().ToString("N"));
    private readonly string _music;
    private readonly ConcurrentDictionary<string, int> _measured = new(StringComparer.OrdinalIgnoreCase);
    private readonly AudioDurations _durations;
    private readonly ConcurrentQueue<IReadOnlyDictionary<string, double?>> _batches = new();
    private readonly CommandRouter _router;

    public AudioDurationsTests()
    {
        _music = Path.Combine(_folder, "Música");
        Directory.CreateDirectory(_music);
        // Mesura inventada: la durada és la mida del fitxer en segons; «malmes» no es pot llegir.
        _durations = new AudioDurations(path =>
        {
            _measured.AddOrUpdate(path, 1, (_, n) => n + 1);
            return path.Contains("malmes") ? null : TimeSpan.FromSeconds(new FileInfo(path).Length);
        });
        _durations.Measured += batch => _batches.Enqueue(batch);

        var store = new ConfigStore(new ConfigRepository(Path.Combine(_folder, "dades", "config.json")));
        store.Load();
        _router = new CommandRouter(new EngineState([new Player("1")], [new Recorder("1")], durations: _durations), () => { }, () => [], config: store);
        Ok($$"""{ "v": 1, "id": 1, "cmd": "explorer.setFolders", "kind": "audios", "folders": [{ "path": "{{Json(_music)}}" }] }""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static string Json(string path) => path.Replace("\\", "\\\\");

    private JsonObject Ok(string json)
    {
        var r = _router.Handle(json);
        Assert.True(r["ok"]!.GetValue<bool>(), r.ToJsonString());
        return r["result"]!.AsObject();
    }

    private JsonObject List() => Ok($$"""{ "v": 1, "id": 2, "cmd": "explorer.list", "kind": "audios", "path": "{{Json(_music)}}" }""");

    private string Write(string name, int bytes)
    {
        string path = Path.Combine(_music, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static double? Duration(JsonObject listing, string name) =>
        listing["files"]!.AsArray().Single(f => f!["name"]!.GetValue<string>() == name)!["duration"]?.GetValue<double>();

    private Dictionary<string, double?> AllMeasured() =>
        _batches.SelectMany(b => b).ToDictionary(p => Path.GetFileName(p.Key), p => p.Value);

    private static bool WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) Thread.Sleep(50);
        return condition();
    }

    [Fact]
    public void La_primera_vegada_les_durades_arriben_despres_i_la_segona_ja_hi_son()
    {
        Write("a.mp3", 3);
        Write("b.mp3", 7);
        Write("malmes.mp3", 1);

        var first = List();
        Assert.Equal(3, first["durationsPending"]!.GetValue<int>());
        Assert.Null(Duration(first, "a.mp3")); // encara no se sap: no s'espera mai a mesurar

        Assert.True(WaitFor(() => AllMeasured().Count == 3));
        var measured = AllMeasured();
        Assert.Equal(3, measured["a.mp3"]);
        Assert.Equal(7, measured["b.mp3"]);
        Assert.Null(measured["malmes.mp3"]); // no s'ha pogut llegir

        var second = List();
        Assert.Equal(0, second["durationsPending"]!.GetValue<int>());
        Assert.Equal(3, Duration(second, "a.mp3"));
        Assert.Equal(7, Duration(second, "b.mp3"));
        Assert.Null(Duration(second, "malmes.mp3"));
        Assert.All(_measured.Values, n => Assert.Equal(1, n)); // cada fitxer, una sola vegada
    }

    [Fact]
    public void Un_fitxer_que_canvia_es_torna_a_mesurar()
    {
        string a = Write("a.mp3", 3);
        List();
        Assert.True(WaitFor(() => AllMeasured().Count == 1));

        File.WriteAllBytes(a, new byte[5]); // una altra mida
        var again = List();

        Assert.Equal(1, again["durationsPending"]!.GetValue<int>());
        Assert.True(WaitFor(() => _measured[a] == 2));
        Assert.True(WaitFor(() => Duration(List(), "a.mp3") == 5));
    }

    [Fact]
    public void La_durada_d_un_fitxer_de_veritat_es_llegeix_sense_descodificar()
    {
        string wav = TestAudio.CreateWav(48000, 2, 2.5, floatFormat: false);
        try
        {
            Assert.Equal(2.5, TrackInfo.QuickDuration(wav)!.Value.TotalSeconds, 0.05);
            Assert.Null(TrackInfo.QuickDuration(Write("no-es-audio.mp3", 10)));
            Assert.Null(TrackInfo.QuickDuration(Path.Combine(_music, "no-hi-es.mp3")));
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public void Les_llistes_desades_no_porten_durada()
    {
        var store = new ConfigStore(new ConfigRepository(Path.Combine(_folder, "dades", "config.json")));
        store.Load();
        File.WriteAllText(Path.Combine(_music, "Dilluns.obpl"), "{}");
        var router = new CommandRouter(new EngineState([], [], durations: _durations), () => { }, () => [], config: store);
        Assert.True(router.Handle($$"""{ "v": 1, "id": 1, "cmd": "explorer.setFolders", "kind": "lists", "folders": [{ "path": "{{Json(_music)}}" }] }""")["ok"]!.GetValue<bool>());

        var r = router.Handle($$"""{ "v": 1, "id": 2, "cmd": "explorer.list", "kind": "lists", "path": "{{Json(_music)}}" }""")["result"]!;

        Assert.Equal(0, r["durationsPending"]!.GetValue<int>());
        Assert.Empty(_measured);
    }
}
