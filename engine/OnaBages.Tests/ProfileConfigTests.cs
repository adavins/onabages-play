using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>
/// Perfils (fase 4, pas 1a; D-057): config.json (versió 2) amb el que és de l'ordinador,
/// perfils\&lt;id&gt;\perfil.json amb la resta, i la conversió de la configuració d'abans.
/// </summary>
public class ProfileConfigTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "perfils-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigRepository _repository;

    public ProfileConfigTests()
    {
        Directory.CreateDirectory(_folder);
        _repository = new ConfigRepository(Path.Combine(_folder, "config.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static JsonObject ReadJson(string path) => (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;

    /// <summary>Un config.json com el de la fase 2 i 3 (versió 1), amb una mica de tot.</summary>
    private const string Version1Config = """
        {
          "schemaVersion": 1,
          "players": [ { "id": "1", "device": { "id": "{sb}", "name": "Sound Blaster" } } ],
          "recorders": [ { "id": "1", "device": { "id": "{in}", "name": "Línia" } } ],
          "cue": [ { "id": "cue", "device": { "id": "{rt}", "name": "Realtek" } } ],
          "cueSettings": { "volumeDb": -6 },
          "knownDevices": [ { "id": "{sb}", "name": "Sound Blaster", "kind": "output", "status": "active" } ],
          "ui": { "zoom": 0.7, "recordersPerRow": 4, "endWarningSeconds": 15 },
          "layout": { "players": 4, "recorders": 3 },
          "program": { "name": "La Central" },
          "editor": { "device": { "id": "{rt}", "name": "Realtek" } },
          "recorderSettings": [ { "id": "1", "name": "Entrevista", "folder": "D:\\Gravacions", "gainDb": 3 } ]
        }
        """;

    private void WriteVersion1(string json = Version1Config, params string[] lastLists)
    {
        File.WriteAllText(_repository.FilePath, json);
        Directory.CreateDirectory(Path.Combine(_folder, "llistes"));
        foreach (string name in lastLists)
            File.WriteAllText(Path.Combine(_folder, "llistes", name), "{}");
    }

    // --- Conversió de la versió 1 ---

    [Fact]
    public void La_configuracio_antiga_es_converteix_en_un_perfil_amb_el_nom_del_programa()
    {
        WriteVersion1();

        var loaded = _repository.Load();

        Assert.Null(loaded.Problem);
        var c = loaded.Config;
        Assert.Equal("La Central", c.ProfileId);
        Assert.Equal("La Central", c.ProfileName);
        // No es perd res: ni el que va al perfil...
        Assert.Equal("{sb}", c.Players["1"].Id);
        Assert.Equal("{in}", c.Recorders["1"].Id);
        Assert.Equal("{rt}", c.Cue["cue"].Id);
        Assert.Equal(-6, c.CueVolumeDb);
        Assert.Equal(4, c.PlayerCount);
        Assert.Equal(3, c.RecorderCount);
        Assert.Equal(4, c.RecordersPerRow);
        Assert.Equal(15, c.EndWarningSeconds);
        Assert.Equal("La Central", c.ProgramName);
        Assert.Equal("Entrevista", c.RecorderSettings["1"].Name);
        // ...ni el que és de l'ordinador.
        Assert.Equal(0.7, c.UiZoom);
        Assert.Equal("{rt}", c.EditorDevice!.Id);
        Assert.Single(c.KnownDevices);
    }

    [Fact]
    public void Despres_de_convertir_cada_cosa_es_al_seu_fitxer()
    {
        WriteVersion1();

        _repository.Load();

        var machine = ReadJson(_repository.FilePath);
        Assert.Equal(2, machine["schemaVersion"]!.GetValue<int>());
        Assert.Equal("La Central", machine["activeProfile"]!.GetValue<string>());
        Assert.NotNull(machine["knownDevices"]);
        Assert.Null(machine["players"]);
        Assert.Null(machine["program"]);

        var profile = ReadJson(_repository.ProfileFilePath("La Central"));
        Assert.Equal(1, profile["schemaVersion"]!.GetValue<int>());
        Assert.Equal("La Central", profile["name"]!.GetValue<string>());
        Assert.NotNull(profile["players"]);
        Assert.Null(profile["knownDevices"]);
        Assert.Null(profile["editor"]);

        // Còpia del fitxer d'abans, per si de cas.
        Assert.Contains("\"schemaVersion\": 1", File.ReadAllText(_repository.Version1CopyPath));
    }

    [Fact]
    public void Tornar_a_llegir_despres_de_convertir_dona_el_mateix()
    {
        WriteVersion1();
        var first = _repository.Load().Config;

        var second = _repository.Load();

        Assert.Null(second.Problem);
        Assert.Equal(first.ProfileId, second.Config.ProfileId);
        Assert.Equal(first.PlayerCount, second.Config.PlayerCount);
        Assert.Equal(first.Players["1"], second.Config.Players["1"]);
        Assert.Equal(first.UiZoom, second.Config.UiZoom);
        Assert.Equal(first.RecorderSettings["1"], second.Config.RecorderSettings["1"]);
    }

    [Fact]
    public void Sense_nom_de_programa_el_perfil_es_diu_Principal()
    {
        WriteVersion1("""{ "schemaVersion": 1, "players": [] }""");

        var c = _repository.Load().Config;

        Assert.Equal("Principal", c.ProfileId);
        Assert.Equal("Principal", c.ProfileName);
        Assert.True(File.Exists(_repository.ProfileFilePath("Principal")));
    }

    [Fact]
    public void Les_ultimes_llistes_passen_a_la_carpeta_del_perfil_i_la_resta_no_es_toca()
    {
        WriteVersion1(Version1Config, "reproductor-1.obpl", "reproductor-4.obpl", "Matins.obpl");

        var c = _repository.Load().Config;

        string lists = _repository.LastListsFolder(c.ProfileId);
        Assert.True(File.Exists(Path.Combine(lists, "reproductor-1.obpl")));
        Assert.True(File.Exists(Path.Combine(lists, "reproductor-4.obpl")));
        Assert.False(File.Exists(Path.Combine(_folder, "llistes", "reproductor-1.obpl")));
        // Una llista desada per l'operador a la mateixa carpeta es queda on és.
        Assert.True(File.Exists(Path.Combine(_folder, "llistes", "Matins.obpl")));
        Assert.False(File.Exists(Path.Combine(lists, "Matins.obpl")));
    }

    [Fact]
    public void Un_nom_de_programa_amb_caracters_prohibits_dona_una_carpeta_valida()
    {
        WriteVersion1("""{ "schemaVersion": 1, "program": { "name": "Esports: 1a/2a part?" } }""");

        var c = _repository.Load().Config;

        Assert.Equal("Esports- 1a-2a part-", c.ProfileId);
        Assert.Equal("Esports: 1a/2a part?", c.ProfileName);
        Assert.True(File.Exists(_repository.ProfileFilePath(c.ProfileId)));
    }

    // --- Lectura i escriptura amb perfils ---

    [Fact]
    public void Sense_cap_fitxer_es_fa_servir_el_perfil_Principal()
    {
        var loaded = _repository.Load();

        Assert.Null(loaded.Problem);
        Assert.Equal(ConfigRepository.DefaultProfileId, loaded.Config.ProfileId);
        Assert.Equal(2, loaded.Config.PlayerCount);
    }

    [Fact]
    public void Desar_crea_el_perfil_i_config_json()
    {
        var c = _repository.Load().Config;
        c.Players["1"] = new DeviceRef("{x}", "X");
        c.UiZoom = 1.25;

        _repository.Save(c);

        Assert.Equal("{x}", ReadJson(_repository.ProfileFilePath("Principal"))["players"]![0]!["device"]!["id"]!.GetValue<string>());
        Assert.Equal(1.25, ReadJson(_repository.FilePath)["ui"]!["zoom"]!.GetValue<double>());
        Assert.Null(ReadJson(_repository.FilePath)["ui"]!["recordersPerRow"]);
    }

    [Fact]
    public void Si_el_perfil_actiu_ja_no_hi_es_es_fa_servir_un_altre_que_hi_hagi()
    {
        var c = _repository.Load().Config;
        c.ProfileId = "Esports";
        c.ProfileName = "Esports";
        c.PlayerCount = 5;
        _repository.Save(c);
        File.WriteAllText(_repository.FilePath, """{ "schemaVersion": 2, "activeProfile": "Ja no existeix" }""");

        var loaded = _repository.Load();

        Assert.Null(loaded.Problem);
        Assert.Equal("Esports", loaded.Config.ProfileId);
        Assert.Equal(5, loaded.Config.PlayerCount);
    }

    [Fact]
    public void Un_perfil_malmes_dona_avis_i_es_conserva_el_que_es_de_lordinador()
    {
        var c = _repository.Load().Config;
        c.UiZoom = 0.8;
        c.PlayerCount = 4;
        _repository.Save(c);
        string profile = _repository.ProfileFilePath(c.ProfileId);
        File.WriteAllText(profile, "{ trencat");

        var loaded = _repository.Load();

        Assert.NotNull(loaded.Problem);
        Assert.Contains("perfil", loaded.Problem);
        Assert.Equal(0.8, loaded.Config.UiZoom);
        Assert.Equal(2, loaded.Config.PlayerCount); // el valor per defecte
        Assert.Equal("{ trencat", File.ReadAllText(profile + ".malmes"));
    }

    [Fact]
    public void Un_perfil_dun_versio_desconeguda_no_es_llegeix()
    {
        var c = _repository.Load().Config;
        _repository.Save(c);
        File.WriteAllText(_repository.ProfileFilePath(c.ProfileId), """{ "schemaVersion": 7 }""");

        var loaded = _repository.Load();

        Assert.Contains("7", loaded.Problem);
    }

    [Fact]
    public void Un_activeProfile_amb_camins_no_es_fa_servir()
    {
        File.WriteAllText(_repository.FilePath, """{ "schemaVersion": 2, "activeProfile": "..\\..\\Windows" }""");

        var loaded = _repository.Load();

        Assert.Equal(ConfigRepository.DefaultProfileId, loaded.Config.ProfileId);
    }

    // --- Noms de carpeta ---

    [Theory]
    [InlineData("La Central", "La Central")]
    [InlineData("  Magazín   dels dissabtes. ", "Magazín dels dissabtes")]
    [InlineData("a<b>c", "a-b-c")]
    [InlineData("", "Perfil")]
    [InlineData("...", "Perfil")]
    [InlineData("con", "con (perfil)")]
    public void Nom_de_carpeta_per_a_un_perfil_nou(string name, string expected)
    {
        Assert.Equal(expected, _repository.NewProfileId(name));
    }

    [Fact]
    public void Si_la_carpeta_ja_existeix_sen_fa_una_altra()
    {
        Directory.CreateDirectory(_repository.ProfileFolder("Esports"));
        Directory.CreateDirectory(_repository.ProfileFolder("Esports (2)"));

        Assert.Equal("Esports (3)", _repository.NewProfileId("Esports"));
    }

    [Fact]
    public void Un_nom_molt_llarg_es_retalla()
    {
        string id = _repository.NewProfileId(new string('x', 100));

        Assert.True(ConfigRepository.IsValidProfileId(id));
        Assert.True(id.Length <= ConfigRepository.MaxProfileIdLength - 5);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a\\b")]
    [InlineData("a/b")]
    [InlineData(" a")]
    [InlineData("a.")]
    [InlineData("NUL")]
    [InlineData("")]
    public void Identificadors_no_valids(string id)
    {
        Assert.False(ConfigRepository.IsValidProfileId(id));
    }

    // --- Protocol ---

    [Fact]
    public void Settings_get_diu_el_perfil_actiu()
    {
        WriteVersion1();
        var store = new ConfigStore(_repository);
        store.Load();
        var state = new EngineState([new Player("1")], [new Recorder("1")]);
        var router = new CommandRouter(state, () => { }, () => [], config: store);

        var r = router.Handle("""{ "v": 1, "id": 1, "cmd": "settings.get" }""");

        var profile = r["result"]!["settings"]!["profile"]!;
        Assert.Equal("La Central", profile["id"]!.GetValue<string>());
        Assert.Equal("La Central", profile["name"]!.GetValue<string>());
    }
}
