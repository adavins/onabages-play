using System.Text;
using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>Desar i obrir llistes (.obpl) i importar M3U (pas 5 de la fase 2, D-035).</summary>
public class PlaylistFilesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "llistes-" + Guid.NewGuid().ToString("N"));

    public PlaylistFilesTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>Fitxer «d'àudio» buit (per a les llistes només compta que existeixi).</summary>
    private string Audio(string relative)
    {
        string path = Path.Combine(_folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    private static PlaylistItem Item(string path, bool loop = false, bool autoNext = false) =>
        new("t1", path, Path.GetFileNameWithoutExtension(path), null, null, TrackStatus.Ok, null, loop, autoNext);

    [Fact]
    public void Desar_i_obrir_conserva_les_rutes_i_les_marques()
    {
        string a = Audio(@"musica\Una cançó.mp3"), b = Audio(@"musica\Dues.mp3");
        string list = Path.Combine(_folder, "Programa.obpl");

        PlaylistFiles.Write(list, [Item(a, loop: true), Item(b, autoNext: true)]);
        var entries = PlaylistFiles.Read(list);

        Assert.Equal([a, b], entries.Select(e => e.Path));
        Assert.True(entries[0].Loop);
        Assert.True(entries[1].AutoNext);
        var json = JsonNode.Parse(File.ReadAllText(list))!;
        Assert.Equal(PlaylistFiles.SchemaVersion, json["schemaVersion"]!.GetValue<int>());
        Assert.Equal(@"musica\Una cançó.mp3", json["items"]![0]!["relativePath"]!.GetValue<string>());
    }

    [Fact]
    public void Si_es_mou_la_llista_amb_la_musica_la_troba_per_la_ruta_relativa()
    {
        string a = Audio(@"original\musica\A.mp3");
        PlaylistFiles.Write(Path.Combine(_folder, "original", "Llista.obpl"), [Item(a)]);

        // Es mou tota la carpeta (p. ex. a un altre disc): la ruta completa desada ja no hi és.
        Directory.Move(Path.Combine(_folder, "original"), Path.Combine(_folder, "mogut"));
        var entries = PlaylistFiles.Read(Path.Combine(_folder, "mogut", "Llista.obpl"));

        Assert.Equal(Path.Combine(_folder, "mogut", "musica", "A.mp3"), entries[0].Path);
    }

    [Fact]
    public void Importa_m3u8_amb_rutes_relatives_i_informacio()
    {
        string a = Audio(@"m\A.mp3"), b = Audio(@"m\Sub\B.mp3");
        string m3u = Path.Combine(_folder, "m", "llista.m3u8");
        File.WriteAllText(m3u, $"#EXTM3U\r\n#EXTINF:123,Artista - A\r\nA.mp3\r\n\r\nSub\\B.mp3\r\n{a}\r\nhttp://radio.example/stream\r\n", new UTF8Encoding(true));

        var entries = PlaylistFiles.Read(m3u);

        Assert.Equal([a, b, a], entries.Select(e => e.Path));
    }

    [Fact]
    public void Importa_m3u_antic_que_no_es_utf8()
    {
        string a = Audio(@"Cançó d'estiu.mp3");
        string m3u = Path.Combine(_folder, "antic.m3u");
        File.WriteAllBytes(m3u, Encoding.Latin1.GetBytes("Cançó d'estiu.mp3\r\n"));

        Assert.Equal(a, PlaylistFiles.Read(m3u).Single().Path);
    }

    [Fact]
    public void Una_carpeta_s_hi_afegeix_amb_les_subcarpetes_en_l_ordre_de_l_explorador()
    {
        string p10 = Audio(@"Programa\Pista 10.mp3"), p2 = Audio(@"Programa\pista 2.wav");
        string sub = Audio(@"Programa\Falques\Falca.mp3");
        Audio(@"Programa\notes.txt");                   // no és àudio
        Audio(@"Programa\llista.m3u");                  // les llistes de dins no s'hi afegeixen
        string hidden = Audio(@"Programa\ocult.mp3");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        Assert.Equal([p2, p10, sub], PlaylistFiles.AudioFilesInFolder(Path.Combine(_folder, "Programa")));

        // Per l'ordre playlist.add (arrossegar la carpeta a la llista).
        var state = new EngineState([new Player("1", _ => new FakeOutput())]);
        var router = new CommandRouter(state, () => { }, () => []);
        string folder = Path.Combine(_folder, "Programa").Replace("\\", "\\\\");
        router.Handle($$"""{ "v": 1, "id": 1, "cmd": "playlist.add", "player": "1", "paths": ["{{folder}}"] }""");
        Assert.Equal(["pista 2", "Pista 10", "Falca"], state.FindPlayer("1")!.Playlist.Items.Select(i => i.Name));
    }

    [Fact]
    public void Una_llista_malmesa_o_d_una_altra_versio_dona_un_error_entenedor()
    {
        string bad = Path.Combine(_folder, "malmesa.obpl");
        File.WriteAllText(bad, "{ això no és JSON");
        string future = Path.Combine(_folder, "futura.obpl");
        File.WriteAllText(future, """{ "schemaVersion": 99, "items": [] }""");

        Assert.Contains("malmès", Assert.Throws<PlaylistFileException>(() => PlaylistFiles.Read(bad)).Message);
        Assert.Contains("versió", Assert.Throws<PlaylistFileException>(() => PlaylistFiles.Read(future)).Message);
        Assert.Throws<PlaylistFileException>(() => PlaylistFiles.Read(Path.Combine(_folder, "no.obpl")));
    }

    [Fact]
    public void Ordres_desar_obrir_i_afegir_una_llista()
    {
        var state = new EngineState([new Player("1", _ => new FakeOutput()), new Player("2", _ => new FakeOutput())]);
        var router = new CommandRouter(state, () => { }, () => []);
        var playlist = state.FindPlayer("1")!.Playlist;
        playlist.Add([Audio("A.mp3"), Audio("B.mp3")]);
        playlist.SetFlags(playlist.Items[1].Id, autoNext: true);
        string list = Path.Combine(_folder, "Desada").Replace("\\", "\\\\");

        var r = router.Handle($$"""{ "v": 1, "id": 1, "cmd": "playlist.save", "player": "1", "path": "{{list}}" }""");
        Assert.EndsWith(".obpl", r["result"]!["path"]!.GetValue<string>()); // s'hi afegeix l'extensió
        string saved = r["result"]!["path"]!.GetValue<string>().Replace("\\", "\\\\");

        // Obrir substituint (per defecte).
        router.Handle($$"""{ "v": 1, "id": 2, "cmd": "playlist.load", "player": "2", "path": "{{saved}}" }""");
        var other = state.FindPlayer("2")!.Playlist;
        Assert.Equal(["A", "B"], other.Items.Select(i => i.Name));
        Assert.True(other.Items[1].AutoNext);

        // Obrir afegint al final.
        r = router.Handle($$"""{ "v": 1, "id": 3, "cmd": "playlist.load", "player": "2", "path": "{{saved}}", "replace": false }""");
        Assert.Equal(4, other.Items.Count);
        Assert.Equal(0, r["result"]!["missing"]!.GetValue<int>());

        // Una llista dins de playlist.add s'hi desplega (p. ex. arrossegada a la llista).
        router.Handle($$"""{ "v": 1, "id": 4, "cmd": "playlist.add", "player": "2", "paths": ["{{saved}}"], "beforeId": "{{other.Items[0].Id}}" }""");
        Assert.Equal(6, other.Items.Count);

        r = router.Handle($$"""{ "v": 1, "id": 5, "cmd": "playlist.load", "player": "2", "path": "C:\\no\\hi\\es.obpl" }""");
        Assert.Equal("file_error", r["error"]!["code"]!.GetValue<string>());
    }
}
