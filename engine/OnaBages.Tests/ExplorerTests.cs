using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>Explorador del panell lateral (fase 4, pas 2a; D-063): explorer.get, setFolders i list.</summary>
public class ExplorerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "explorador-" + Guid.NewGuid().ToString("N"));
    private readonly string _music;
    private readonly ConfigRepository _repository;
    private readonly CommandRouter _router;

    public ExplorerTests()
    {
        _music = Path.Combine(_folder, "Música");
        Directory.CreateDirectory(_music);
        _repository = new ConfigRepository(Path.Combine(_folder, "dades", "config.json"));
        var store = new ConfigStore(_repository);
        store.Load();
        _router = new CommandRouter(new EngineState([new Player("1")], [new Recorder("1")]), () => { }, () => [], config: store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static string Json(string path) => path.Replace("\\", "\\\\");

    private JsonObject Send(string json) => _router.Handle(json);

    private JsonObject Ok(string json)
    {
        var r = Send(json);
        Assert.True(r["ok"]!.GetValue<bool>(), r.ToJsonString());
        return r["result"]!.AsObject();
    }

    private static string ErrorCode(JsonObject r) => r["error"]!["code"]!.GetValue<string>();

    private JsonObject SetAudioFolders(params string[] paths) => Ok($$"""
        { "v": 1, "id": 1, "cmd": "explorer.setFolders", "kind": "audios",
          "folders": [{{string.Join(", ", paths.Select(p => $$"""{ "path": "{{Json(p)}}" }"""))}}] }
        """);

    private JsonObject List(string path, string kind = "audios") =>
        Send($$"""{ "v": 1, "id": 2, "cmd": "explorer.list", "kind": "{{kind}}", "path": "{{Json(path)}}" }""");

    private static string[] Names(JsonObject listing, string field) =>
        listing[field]!.AsArray().Select(e => e!["name"]!.GetValue<string>()).ToArray();

    private void Touch(params string[] relative)
    {
        foreach (string r in relative)
        {
            string path = Path.Combine(_music, r);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
    }

    // --- Carpetes de cada pestanya ---

    [Fact]
    public void Al_principi_no_hi_ha_cap_carpeta()
    {
        var r = Ok("""{ "v": 1, "id": 1, "cmd": "explorer.get" }""");

        Assert.Empty(r["audios"]!.AsArray());
        Assert.Empty(r["lists"]!.AsArray());
    }

    [Fact]
    public void Les_carpetes_es_desen_al_perfil_amb_el_nom_de_la_carpeta_per_defecte()
    {
        string jingles = Path.Combine(_music, "Sintonies");
        Directory.CreateDirectory(jingles);

        var r = Ok($$"""
            { "v": 1, "id": 1, "cmd": "explorer.setFolders", "kind": "audios",
              "folders": [ { "path": "{{Json(jingles)}}" }, { "name": "  La   música ", "path": "{{Json(_music)}}" } ] }
            """);

        var audios = r["audios"]!.AsArray();
        Assert.Equal("Sintonies", audios[0]!["name"]!.GetValue<string>());
        Assert.True(audios[0]!["exists"]!.GetValue<bool>());
        Assert.Equal("La música", audios[1]!["name"]!.GetValue<string>());
        var saved = _repository.Load().Config;
        Assert.Equal([new ExplorerFolder("Sintonies", jingles), new ExplorerFolder("La música", _music)], saved.AudioFolders);
        Assert.Empty(saved.ListFolders);
    }

    [Fact]
    public void Una_carpeta_que_ara_no_hi_es_es_pot_desar_i_es_marca()
    {
        string missing = @"Z:\No hi és\Talls";

        var r = SetAudioFolders(missing);

        Assert.False(r["audios"]![0]!["exists"]!.GetValue<bool>());
        Assert.Equal("file_error", ErrorCode(List(missing)));
    }

    [Theory]
    [InlineData("""{ "kind": "audios", "folders": [ { "path": "Música" } ] }""")]
    [InlineData("""{ "kind": "videos", "folders": [] }""")]
    [InlineData("""{ "kind": "audios" }""")]
    [InlineData("""{ "kind": "audios", "folders": [ { "path": "C:\\Música" }, { "path": "c:\\música\\" } ] }""")]
    [InlineData("""{ "kind": "audios", "folders": [ { "name": "Un nom de carpeta molt i molt i molt llarg", "path": "C:\\M" } ] }""")]
    public void Carpetes_no_valides_donen_bad_params(string fields)
    {
        var msg = JsonNode.Parse(fields)!.AsObject();
        msg["v"] = 1; msg["id"] = 1; msg["cmd"] = "explorer.setFolders";

        Assert.Equal("bad_params", ErrorCode(Send(msg.ToJsonString())));
    }

    [Fact]
    public void Com_a_molt_vint_carpetes()
    {
        var paths = Enumerable.Range(1, EngineConfig.MaxExplorerFolders + 1).Select(i => $@"C:\Carpeta {i}");
        var r = Send($$"""
            { "v": 1, "id": 1, "cmd": "explorer.setFolders", "kind": "lists",
              "folders": [{{string.Join(", ", paths.Select(p => $$"""{ "path": "{{Json(p)}}" }"""))}}] }
            """);

        Assert.Equal("bad_params", ErrorCode(r));
    }

    // --- Contingut d'una carpeta ---

    [Fact]
    public void Llista_subcarpetes_i_audios_en_ordre_natural()
    {
        Touch("Pista 10.mp3", "Pista 2.mp3", "pista 1.WAV", "notes.txt", "llista.obpl", @"Talls\a.mp3", @"Sintonies\b.mp3");
        SetAudioFolders(_music);

        var r = List(_music);

        Assert.True(r["ok"]!.GetValue<bool>(), r.ToJsonString());
        var listing = r["result"]!.AsObject();
        Assert.Equal(["Sintonies", "Talls"], Names(listing, "folders"));
        Assert.Equal(["pista 1.WAV", "Pista 2.mp3", "Pista 10.mp3"], Names(listing, "files"));
        Assert.Equal(1, listing["files"]![0]!["size"]!.GetValue<long>());
        Assert.False(listing["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void A_la_pestanya_Llistes_nomes_surten_llistes()
    {
        Touch("a.mp3", "Dilluns.obpl", "antiga.m3u", "b.m3u8");
        Ok($$"""{ "v": 1, "id": 1, "cmd": "explorer.setFolders", "kind": "lists", "folders": [ { "path": "{{Json(_music)}}" } ] }""");

        var listing = List(_music, "lists")["result"]!.AsObject();

        Assert.Equal(["antiga.m3u", "b.m3u8", "Dilluns.obpl"], Names(listing, "files"));
    }

    [Fact]
    public void Els_fitxers_ocults_no_surten()
    {
        Touch("visible.mp3", "ocult.mp3");
        File.SetAttributes(Path.Combine(_music, "ocult.mp3"), FileAttributes.Hidden);
        SetAudioFolders(_music);

        Assert.Equal(["visible.mp3"], Names(List(_music)["result"]!.AsObject(), "files"));
    }

    [Fact]
    public void Es_pot_entrar_a_una_subcarpeta()
    {
        Touch(@"Talls\Entrevista.mp3");
        SetAudioFolders(_music);

        var listing = List(Path.Combine(_music, "Talls"))["result"]!.AsObject();

        Assert.Equal(["Entrevista.mp3"], Names(listing, "files"));
    }

    // --- Només dins de les carpetes configurades ---

    [Fact]
    public void Fora_de_les_carpetes_no_es_pot_llegir()
    {
        SetAudioFolders(_music);

        Assert.Equal("bad_params", ErrorCode(List(_folder)));
        Assert.Equal("bad_params", ErrorCode(List(@"C:\Windows")));
        Assert.Equal("bad_params", ErrorCode(List(Path.Combine(_music, "..", "dades"))));
    }

    [Fact]
    public void Una_carpeta_germana_amb_el_mateix_principi_no_hi_compta()
    {
        string sibling = _music + "2";
        Directory.CreateDirectory(sibling);
        SetAudioFolders(_music);

        Assert.Equal("bad_params", ErrorCode(List(sibling)));
    }

    [Fact]
    public void Les_carpetes_de_cada_pestanya_van_a_part()
    {
        SetAudioFolders(_music);

        Assert.Equal("bad_params", ErrorCode(List(_music, "lists")));
    }

    [Fact]
    public void Una_ruta_relativa_no_val()
    {
        SetAudioFolders(_music);

        Assert.Equal("bad_params", ErrorCode(List("Música")));
    }
}
