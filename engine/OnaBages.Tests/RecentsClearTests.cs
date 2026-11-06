using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>
/// Netejar la llista de Recents (fase 5, pas 11a; D-089): es treuen els desats i els no trobats; els no desats
/// només si es demana. Els fitxers no es toquen mai, i el pendent d'un enregistrador es queda sempre.
/// </summary>
public class RecentsClearTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "recents-" + Guid.NewGuid().ToString("N"));

    public RecentsClearTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private string File(string name)
    {
        string path = Path.Combine(_folder, name);
        System.IO.File.WriteAllText(path, "mp3");
        return path;
    }

    /// <summary>Una llista amb: un desat, un no trobat, dos no desats i el pendent d'un enregistrador.</summary>
    private (RecordingList List, string Pending) NewList()
    {
        string pending = File("pendent.mp3");
        var list = new RecordingList(isPending: p => p == pending, synchronous: true);
        void Add(string path) => list.Add(new FinishedRecording("1", path, DateTimeOffset.Now, TimeSpan.FromSeconds(1), 3, true));
        Add(File("desat-abans.mp3"));
        Add(Path.Combine(_folder, "no-hi-es.mp3"));
        Add(File("no-desat-1.mp3"));
        Add(File("no-desat-2.mp3"));
        Add(pending);
        var saved = list.Items.First(i => i.Path.EndsWith("desat-abans.mp3"));
        list.Save(saved.Id, Path.Combine(_folder, "desats", "Programa.mp3"));
        return (list, pending);
    }

    [Fact]
    public void Treu_els_desats_i_els_no_trobats_i_deixa_els_no_desats()
    {
        var (list, pending) = NewList();

        var (removed, unsavedLeft) = list.Clear(includeUnsaved: false);

        Assert.Equal(2, removed);
        Assert.Equal(2, unsavedLeft);                                  // els dos no desats (el pendent no compta)
        Assert.Equal(["no-desat-1.mp3", "no-desat-2.mp3", "pendent.mp3"], list.Items.Select(i => Path.GetFileName(i.Path)).Order());
        Assert.True(System.IO.File.Exists(Path.Combine(_folder, "desats", "Programa.mp3"))); // el fitxer no es toca
    }

    [Fact]
    public void Amb_els_no_desats_nomes_queda_el_pendent_i_els_fitxers_hi_son()
    {
        var (list, pending) = NewList();

        var (removed, unsavedLeft) = list.Clear(includeUnsaved: true);

        Assert.Equal(4, removed);
        Assert.Equal(0, unsavedLeft);
        Assert.Equal(pending, Assert.Single(list.Items).Path);
        Assert.True(System.IO.File.Exists(Path.Combine(_folder, "no-desat-1.mp3")));
        Assert.True(System.IO.File.Exists(Path.Combine(_folder, "no-desat-2.mp3")));
    }

    [Fact]
    public void Una_llista_neta_no_avisa_de_cap_canvi()
    {
        var list = new RecordingList(synchronous: true);
        int changes = 0;
        list.Changed += () => changes++;

        Assert.Equal((0, 0), list.Clear(includeUnsaved: true));
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Ordre_i_ajust()
    {
        var repository = new ConfigRepository(Path.Combine(_folder, "dades", "config.json"));
        var store = new ConfigStore(repository);
        store.Load();
        var (list, _) = NewList();
        var router = new CommandRouter(new EngineState([], []), () => { }, () => [], recordings: list, config: store);

        var r = router.Handle("""{ "v": 1, "id": 1, "cmd": "recordings.clear" }""")["result"]!;
        Assert.Equal(2, r["removed"]!.GetValue<int>());
        Assert.Equal(2, r["unsavedLeft"]!.GetValue<int>());
        r = router.Handle("""{ "v": 1, "id": 2, "cmd": "recordings.clear", "includeUnsaved": true }""")["result"]!;
        Assert.Equal(2, r["removed"]!.GetValue<int>());
        Assert.Equal("bad_params", router.Handle("""{ "v": 1, "id": 3, "cmd": "recordings.clear", "includeUnsaved": "sí" }""")["error"]!["code"]!.GetValue<string>());

        var settings = router.Handle("""{ "v": 1, "id": 4, "cmd": "settings.get" }""")["result"]!["settings"]!;
        Assert.False(settings["recents"]!["clearOnExit"]!.GetValue<bool>());          // per defecte, no
        settings = router.Handle("""{ "v": 1, "id": 5, "cmd": "settings.set", "recents": { "clearOnExit": true } }""")["result"]!["settings"]!;
        Assert.True(settings["recents"]!["clearOnExit"]!.GetValue<bool>());
        Assert.True(repository.Load().Config.ClearRecentsOnExit);
        Assert.Contains("clearOnExit", System.IO.File.ReadAllText(Path.Combine(_folder, "dades", "config.json"))); // de l'ordinador
    }
}
