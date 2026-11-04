using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>Separadors de secció a les llistes (pas 9c de la fase 2, D-042).</summary>
public class SectionTests : IDisposable
{
    private const string DeviceId = "{0.0.0.00000000}.{proves}";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "seccions-" + Guid.NewGuid().ToString("N"));
    private FakeOutput? _output;

    public SectionTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static string Wav(double seconds = 0.3) => TestAudio.CreateWav(48000, 2, seconds);

    private Player NewPlayer()
    {
        var player = new Player("1", _ => _output = new FakeOutput(), new TrackAnalyzer(synchronous: true));
        player.SetDevice(DeviceId);
        return player;
    }

    private void PumpUntilStopped(Player player)
    {
        Thread.Sleep(150);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (player.State == PlayerState.Playing && DateTime.UtcNow < deadline)
        {
            _output!.Pump(480);
            player.Tick();
            Thread.Sleep(1);
        }
    }

    // --- Regles ---

    [Fact]
    public void Un_separador_te_titol_i_color_i_no_es_cap_fitxer()
    {
        var list = new Playlist(new TrackAnalyzer(synchronous: true));
        var tracks = list.Add([Wav(), Wav()]);

        string id = list.AddSection("  ESPORTS  ", "blue", beforeId: tracks[1]);

        var items = list.Items;
        Assert.Equal([tracks[0], id, tracks[1]], items.Select(i => i.Id));
        var section = items[1];
        Assert.True(section.IsSection);
        Assert.Equal("ESPORTS", section.Name);
        Assert.Equal("blue", section.Color);
        Assert.Equal(TrackStatus.Ok, section.Status);
        Assert.Equal("", section.Path);

        // No té durada: el total és només el de les pistes.
        Assert.InRange(list.TotalDuration.TotalSeconds, 0.59, 0.61);

        // La comprovació periòdica de fitxers no el marca com a «no trobat».
        list.CheckFiles();
        Assert.Equal(TrackStatus.Ok, list.Find(id)!.Status);
    }

    [Fact]
    public void Es_pot_canviar_el_titol_el_color_i_plegar()
    {
        var list = new Playlist();
        string id = list.AddSection("Cinema");

        list.UpdateSection(id, title: "CINEMA", color: "red", collapsed: true);
        var s = list.Find(id)!;
        Assert.Equal(("CINEMA", "red", true), (s.Name, s.Color, s.Collapsed));

        list.UpdateSection(id, color: "");   // treure el color
        Assert.Null(list.Find(id)!.Color);
        Assert.True(list.Find(id)!.Collapsed); // el que no s'envia no canvia

        // Una pista no és un separador.
        string track = list.Add([@"C:\a\Un.mp3"])[0];
        Assert.Throws<PlayerCommandException>(() => list.UpdateSection(track, title: "x"));
    }

    [Fact]
    public void Un_separador_no_pot_tenir_cue_bucle_ni_avanc()
    {
        var list = new Playlist();
        string id = list.AddSection("Secció");

        Assert.Throws<PlayerCommandException>(() => list.SetFlags(id, cue: true));
        Assert.Throws<PlayerCommandException>(() => list.SetFlags(id, loop: true));
        Assert.Throws<PlayerCommandException>(() => list.SetFlags(id, autoNext: true));
    }

    [Fact]
    public void La_seguent_se_salta_els_separadors()
    {
        var list = new Playlist();
        var a = list.Add([@"C:\a\Un.mp3"])[0];
        list.AddSection("A");
        list.AddSection("B");
        var b = list.Add([@"C:\a\Dos.mp3"])[0];

        Assert.Equal(b, list.NextAfter(a));

        // Una llista que comença amb un separador: la primera pista.
        var other = new Playlist();
        other.AddSection("Inici");
        var first = other.Add([@"C:\a\Un.mp3"])[0];
        Assert.Equal(first, other.NextAfter(null));

        // Al final, només separadors: no hi ha següent.
        list.AddSection("Final");
        Assert.Null(list.NextAfter(b));
    }

    [Fact]
    public void Els_fitxers_______mp3_es_converteixen_en_separadors()
    {
        Assert.True(Playlist.IsSeparatorFileName(@"C:\música\______.mp3"));
        Assert.True(Playlist.IsSeparatorFileName(@"C:\música\---.mp3"));
        Assert.False(Playlist.IsSeparatorFileName(@"C:\música\__.mp3"));        // massa curt
        Assert.False(Playlist.IsSeparatorFileName(@"C:\música\__intro__.mp3")); // té text: és una pista

        var list = new Playlist();
        var ids = list.Add([@"C:\a\Un.mp3", @"C:\a\______.mp3", @"C:\a\Dos.mp3"]);
        Assert.True(list.Find(ids[1])!.IsSection);
        Assert.Equal(Playlist.DefaultSectionTitle, list.Find(ids[1])!.Name);
    }

    // --- Reproductor ---

    [Fact]
    public void Play_i_doble_clic_no_fan_sonar_un_separador()
    {
        using var player = NewPlayer();
        string section = player.Playlist.AddSection("Inici");
        var tracks = player.Playlist.Add([Wav(), Wav()]);

        Assert.Throws<PlayerCommandException>(() => player.PlayItem(section));

        player.Play(); // sense res sonat: la primera pista (no el separador)
        Assert.Equal(tracks[0], player.CurrentItemId);
    }

    [Fact]
    public void L_avanc_automatic_se_salta_el_separador()
    {
        using var player = NewPlayer();
        var first = player.Playlist.Add([Wav()])[0];
        player.Playlist.AddSection("ESPORTS");
        var sintonia = player.Playlist.Add([Wav()])[0];
        player.Playlist.SetFlags(first, autoNext: true);

        player.Play();
        PumpUntilStopped(player);

        // Ha sonat la sintonia de la secció següent, sense aturar-se al separador (D-042).
        Assert.Equal(sintonia, player.LastPlayedId);
    }

    // --- Fitxers de llista ---

    [Fact]
    public void Una_llista_desada_conserva_els_separadors()
    {
        string a = Path.Combine(_folder, "Un.mp3");
        File.WriteAllBytes(a, [0]);
        var list = new Playlist();
        list.Add([a]);
        string id = list.AddSection("ESPORTS", "green");
        list.UpdateSection(id, collapsed: true);
        string path = Path.Combine(_folder, "Magazin.obpl");

        PlaylistFiles.Write(path, list.Items);
        var entries = PlaylistFiles.Read(path);

        Assert.Equal(2, entries.Count);
        Assert.False(entries[0].IsSection);
        Assert.True(entries[1].IsSection);
        Assert.Equal(("ESPORTS", "green", true), (entries[1].SectionTitle, entries[1].Color, entries[1].Collapsed));
    }

    [Fact]
    public void Un_m3u_del_winamp_amb_______mp3_porta_separadors()
    {
        File.WriteAllText(Path.Combine(_folder, "Winamp.m3u"), "#EXTM3U\r\nSintonia.mp3\r\n______.mp3\r\nCortina.mp3\r\n");

        var entries = PlaylistFiles.Read(Path.Combine(_folder, "Winamp.m3u"));

        Assert.Equal([false, true, false], entries.Select(e => e.IsSection));
    }

    // --- Ordres ---

    [Fact]
    public void Ordres_de_seccions()
    {
        var state = new EngineState([new Player("1", _ => new FakeOutput(), new TrackAnalyzer(synchronous: true))]);
        var router = new CommandRouter(state, () => { }, () => []);
        JsonObject Send(string json) => router.Handle(json);
        string Code(JsonObject r) => r["error"]!["code"]!.GetValue<string>();

        var r = Send("""{ "v": 1, "id": 1, "cmd": "playlist.addSection", "player": "1", "title": "ESPORTS", "color": "orange" }""");
        string id = r["result"]!["id"]!.GetValue<string>();

        var item = state.Players[0].Playlist.Find(id)!;
        Assert.Equal(("ESPORTS", "orange"), (item.Name, item.Color));

        r = Send($$"""{ "v": 1, "id": 2, "cmd": "playlist.updateSection", "player": "1", "itemId": "{{id}}", "collapsed": true, "color": null }""");
        var json = r["result"]!["playlist"]!["items"]![0]!;
        Assert.Equal("section", json["type"]!.GetValue<string>());
        Assert.True(json["collapsed"]!.GetValue<bool>());
        Assert.Null(json["color"]);
        Assert.Equal("ESPORTS", json["name"]!.GetValue<string>()); // el títol no s'ha enviat: no canvia

        Assert.Equal("bad_params", Code(Send("""{ "v": 1, "id": 3, "cmd": "playlist.addSection", "player": "1", "title": "  " }""")));
        Assert.Equal("bad_params", Code(Send("""{ "v": 1, "id": 4, "cmd": "playlist.addSection", "player": "1", "title": "X", "color": "fúcsia" }""")));
        Assert.Equal("bad_state", Code(Send($$"""{ "v": 1, "id": 5, "cmd": "playlist.setFlags", "player": "1", "itemId": "{{id}}", "cue": true }""")));
        Assert.Equal("bad_state", Code(Send($$"""{ "v": 1, "id": 6, "cmd": "player.playItem", "player": "1", "itemId": "{{id}}" }""")));

        // A una pista no se li pot canviar el títol com a secció.
        string track = state.Players[0].Playlist.Add([Wav()])[0];
        Assert.Equal("bad_state", Code(Send($$"""{ "v": 1, "id": 7, "cmd": "playlist.updateSection", "player": "1", "itemId": "{{track}}", "title": "X" }""")));
    }
}
