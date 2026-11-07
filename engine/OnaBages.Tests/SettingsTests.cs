using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>Ordres settings.get i settings.set (zoom, distribució, avís de final), desades a config.json.</summary>
public class SettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "settings-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigRepository _repository;
    private readonly CommandRouter _router;

    public SettingsTests()
    {
        _repository = new ConfigRepository(Path.Combine(_folder, "config.json"));
        var store = new ConfigStore(_repository);
        store.Load();
        // Com el motor real per defecte: dos reproductors i dos enregistradors.
        var state = new EngineState([new Player("1"), new Player("2")], [new Recorder("1"), new Recorder("2")]);
        _router = new CommandRouter(state, () => { }, () => [], config: store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static double Zoom(JsonObject r) => r["result"]!["settings"]!["ui"]!["zoom"]!.GetValue<double>();

    private static string ErrorCode(JsonObject r) => r["error"]!["code"]!.GetValue<string>();

    [Fact]
    public void Per_defecte_el_zoom_es_100()
    {
        var r = _router.Handle("""{ "v": 1, "id": 1, "cmd": "settings.get" }""");

        Assert.Equal(1.0, Zoom(r));
    }

    [Fact]
    public void El_zoom_es_desa_i_es_conserva()
    {
        var r = _router.Handle("""{ "v": 1, "id": 1, "cmd": "settings.set", "ui": { "zoom": 1.25 } }""");

        Assert.Equal(1.25, Zoom(r));
        Assert.Equal(1.25, _repository.Load().Config.UiZoom);
    }

    [Theory]
    [InlineData("0.4")]
    [InlineData("2")]
    [InlineData("\"gran\"")]
    public void Zoom_fora_de_marge_dona_bad_params(string value)
    {
        var r = _router.Handle($$"""{ "v": 1, "id": 1, "cmd": "settings.set", "ui": { "zoom": {{value}} } }""");

        Assert.Equal("bad_params", ErrorCode(r));
        Assert.Equal(1.0, _repository.Load().Config.UiZoom);
    }

    [Fact]
    public void Un_fitxer_antic_sense_ui_es_llegeix_igualment()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(_repository.FilePath, """{ "schemaVersion": 1, "players": [], "recorders": [], "knownDevices": [] }""");

        var loaded = _repository.Load();

        Assert.Null(loaded.Problem);
        Assert.Equal(1.0, loaded.Config.UiZoom);
    }

    // --- Distribució i avís de final (pas 9) ---

    private JsonObject Settings(string json)
    {
        var r = _router.Handle(json);
        Assert.True(r["ok"]!.GetValue<bool>(), r.ToJsonString());
        return r["result"]!["settings"]!.AsObject();
    }

    [Fact]
    public void Valors_per_defecte_de_la_distribucio()
    {
        var s = Settings("""{ "v": 1, "id": 1, "cmd": "settings.get" }""");

        Assert.Equal(2, s["layout"]!["players"]!.GetValue<int>());
        Assert.Equal(2, s["layout"]!["recorders"]!.GetValue<int>());
        Assert.Equal(2, s["ui"]!["recordersPerRow"]!.GetValue<int>());
        Assert.Equal(10, s["ui"]!["endWarningSeconds"]!.GetValue<int>());
        Assert.False(s["restartNeeded"]!.GetValue<bool>());
    }

    [Fact]
    public void Canviar_el_nombre_es_desa_i_cal_reiniciar()
    {
        var s = Settings("""{ "v": 1, "id": 1, "cmd": "settings.set", "layout": { "players": 3, "recorders": 4 }, "ui": { "recordersPerRow": 2, "endWarningSeconds": 15 } }""");

        Assert.True(s["restartNeeded"]!.GetValue<bool>());
        var saved = _repository.Load().Config;
        Assert.Equal(3, saved.PlayerCount);
        Assert.Equal(4, saved.RecorderCount);
        Assert.Equal(15, saved.EndWarningSeconds);
        Assert.Equal(1.0, saved.UiZoom); // el que no s'envia no canvia

        // Tornar al nombre actual: ja no cal reiniciar.
        s = Settings("""{ "v": 1, "id": 2, "cmd": "settings.set", "layout": { "players": 2, "recorders": 2 } }""");
        Assert.False(s["restartNeeded"]!.GetValue<bool>());
    }

    [Fact]
    public void Es_poden_configurar_fins_a_6_enregistradors_en_files_de_3()
    {
        var s = Settings("""{ "v": 1, "id": 1, "cmd": "settings.set", "layout": { "recorders": 6 }, "ui": { "recordersPerRow": 3 } }""");

        Assert.Equal(6, s["layout"]!["recorders"]!.GetValue<int>());
        Assert.Equal(3, s["ui"]!["recordersPerRow"]!.GetValue<int>());
        Assert.True(s["restartNeeded"]!.GetValue<bool>());
        var saved = _repository.Load().Config;
        Assert.Equal(6, saved.RecorderCount);
        Assert.Equal(3, saved.RecordersPerRow);
    }

    [Fact]
    public void Amagar_el_mesurador_dels_enregistradors_inactius_es_desa_al_perfil()
    {
        var s = Settings("""{ "v": 1, "id": 1, "cmd": "settings.get" }""");
        Assert.False(s["ui"]!["hideIdleRecorderMeters"]!.GetValue<bool>()); // per defecte, es veu

        s = Settings("""{ "v": 1, "id": 2, "cmd": "settings.set", "ui": { "hideIdleRecorderMeters": true } }""");

        Assert.True(s["ui"]!["hideIdleRecorderMeters"]!.GetValue<bool>());
        Assert.True(_repository.Load().Config.HideIdleRecorderMeters);
        Assert.Equal(10, s["ui"]!["endWarningSeconds"]!.GetValue<int>()); // el que no s'envia no canvia

        var bad = _router.Handle("""{ "v": 1, "id": 3, "cmd": "settings.set", "ui": { "hideIdleRecorderMeters": "sí" } }""");
        Assert.Equal("bad_params", ErrorCode(bad));
        Assert.True(_repository.Load().Config.HideIdleRecorderMeters);
    }

    [Fact]
    public void La_durada_del_fade_out_es_desa_al_perfil()
    {
        var s = Settings("""{ "v": 1, "id": 1, "cmd": "settings.get" }""");
        Assert.Equal(2000, s["fadeOut"]!["ms"]!.GetValue<int>()); // per defecte, 2 s
        Assert.Equal(Player.MinFadeOutMs, s["fadeOut"]!["minMs"]!.GetValue<int>());
        Assert.Equal(Player.MaxFadeOutMs, s["fadeOut"]!["maxMs"]!.GetValue<int>());

        s = Settings("""{ "v": 1, "id": 2, "cmd": "settings.set", "fadeOut": { "ms": 3500 } }""");

        Assert.Equal(3500, s["fadeOut"]!["ms"]!.GetValue<int>());
        Assert.Equal(3500, _repository.Load().Config.FadeOutMs);
    }

    [Theory]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "settings.set", "fadeOut": { "ms": 400 } }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "settings.set", "fadeOut": { "ms": 10001 } }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "settings.set", "fadeOut": { "ms": "2s" } }""")]
    public void Una_durada_de_fade_out_fora_de_marge_no_canvia_res(string json)
    {
        Assert.Equal("bad_params", ErrorCode(_router.Handle(json)));
        Assert.False(File.Exists(_repository.FilePath));
    }

    [Fact]
    public void L_ordre_dels_panells_es_desa_al_perfil()
    {
        var s = Settings("""{ "v": 1, "id": 1, "cmd": "settings.get" }""");
        Assert.Empty(s["ui"]!["playerOrder"]!.AsArray()); // per defecte, per número
        Assert.Empty(s["ui"]!["recorderOrder"]!.AsArray());

        s = Settings("""{ "v": 1, "id": 2, "cmd": "settings.set", "ui": { "playerOrder": ["2", "1"], "recorderOrder": ["4", "1", "6"] } }""");

        Assert.Equal(["2", "1"], s["ui"]!["playerOrder"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["4", "1", "6"], s["ui"]!["recorderOrder"]!.AsArray().Select(n => n!.GetValue<string>()));
        var saved = _repository.Load().Config;
        Assert.Equal(["2", "1"], saved.PlayerOrder);
        Assert.Equal(["4", "1", "6"], saved.RecorderOrder);
        Assert.False(s["restartNeeded"]!.GetValue<bool>()); // s'aplica de seguida

        // Una llista buida torna a l'ordre per número; el que no s'envia no canvia.
        Settings("""{ "v": 1, "id": 3, "cmd": "settings.set", "ui": { "playerOrder": [] } }""");
        saved = _repository.Load().Config;
        Assert.Empty(saved.PlayerOrder);
        Assert.Equal(["4", "1", "6"], saved.RecorderOrder);
    }

    [Theory]
    [InlineData("""["1", "1"]""")]      // repetit
    [InlineData("""["7"]""")]           // no hi ha reproductor 7
    [InlineData("""["0"]""")]
    [InlineData("""["01"]""")]          // el motor l'escriu «1»
    [InlineData("""[1, 2]""")]          // números, no textos
    [InlineData("\"1,2\"")]             // no és una llista
    [InlineData("""["1","2","3","4","5","6","1"]""")]
    public void Un_ordre_que_no_es_valid_no_canvia_res(string order)
    {
        var r = _router.Handle($$"""{ "v": 1, "id": 1, "cmd": "settings.set", "ui": { "playerOrder": {{order}} } }""");

        Assert.Equal("bad_params", ErrorCode(r));
        Assert.False(File.Exists(_repository.FilePath));
    }

    [Fact]
    public void Un_ordre_malmes_al_perfil_es_llegeix_com_per_numero()
    {
        Settings("""{ "v": 1, "id": 1, "cmd": "settings.set", "ui": { "recorderOrder": ["2", "1"] } }""");
        string profile = Directory.GetFiles(Path.GetDirectoryName(_repository.FilePath)!, "perfil.json", SearchOption.AllDirectories).Single();
        File.WriteAllText(profile, File.ReadAllText(profile).Replace("\"2\",", "\"1\",")); // ["1", "1"]

        Assert.Empty(_repository.Load().Config.RecorderOrder);
    }

    [Fact]
    public void Treure_elements_no_esborra_la_seva_configuracio()
    {
        // Configuració amb 4 enregistradors, cada un amb entrada i nom.
        var config = new EngineConfig { RecorderCount = 4 };
        for (int i = 1; i <= 4; i++)
        {
            config.Recorders[$"{i}"] = new DeviceRef($"{{entrada{i}}}", $"Entrada {i}");
            config.RecorderSettings[$"{i}"] = new RecorderSettings($"Estudi {i}", null, 0);
        }
        _repository.Save(config);
        var store = new ConfigStore(_repository);
        store.Load();
        var router = new CommandRouter(new EngineState([], [new Recorder("1"), new Recorder("2")]), () => { }, () => [], config: store);

        var r = router.Handle("""{ "v": 1, "id": 1, "cmd": "settings.set", "layout": { "recorders": 2 } }""");
        Assert.True(r["ok"]!.GetValue<bool>());

        // Els enregistradors 3 i 4 conserven l'entrada i el nom: si es tornen a posar, tornen tal com eren.
        var saved = _repository.Load().Config;
        Assert.Equal(2, saved.RecorderCount);
        Assert.Equal(new DeviceRef("{entrada4}", "Entrada 4"), saved.Recorders["4"]);
        Assert.Equal("Estudi 3", saved.RecorderSettings["3"].Name);
    }

    [Theory]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "settings.set", "layout": { "players": 0 } }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "settings.set", "layout": { "players": 7 } }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "settings.set", "layout": { "recorders": 7 } }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "settings.set", "ui": { "recordersPerRow": 7 } }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "settings.set", "layout": { "recorders": 2.5 } }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "settings.set", "ui": { "recordersPerRow": 0 } }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "settings.set", "ui": { "endWarningSeconds": 61 } }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "settings.set", "ui": { "zoom": 1.2, "endWarningSeconds": -1 } }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "settings.set" }""")]
    public void Valors_fora_de_marge_no_canvien_res(string json)
    {
        var r = _router.Handle(json);

        Assert.Equal("bad_params", ErrorCode(r));
        Assert.False(File.Exists(_repository.FilePath)); // no s'ha desat res (ni el zoom bo)
    }

    [Fact]
    public void Un_fitxer_amb_valors_fora_de_marge_es_porta_al_limit()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(_repository.FilePath, """
            { "schemaVersion": 1, "layout": { "players": 9, "recorders": 0 }, "ui": { "endWarningSeconds": 500 } }
            """);

        var config = _repository.Load().Config;

        Assert.Equal(EngineConfig.MaxPlayers, config.PlayerCount);
        Assert.Equal(1, config.RecorderCount);
        Assert.Equal(EngineConfig.MaxEndWarningSeconds, config.EndWarningSeconds);
    }

    // --- Nom del programa (pas 10) ---

    [Fact]
    public void Per_defecte_no_hi_ha_nom_de_programa()
    {
        var settings = Settings("""{ "v": 1, "id": 1, "cmd": "settings.get" }""");

        Assert.Equal("", settings["program"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void El_nom_del_programa_es_desa_net()
    {
        var settings = Settings("""{ "v": 1, "id": 1, "cmd": "settings.set", "program": { "name": "  Magazín   dels\ndissabtes " } }""");

        Assert.Equal("Magazín dels dissabtes", settings["program"]!["name"]!.GetValue<string>());
        Assert.Equal("Magazín dels dissabtes", _repository.Load().Config.ProgramName);
    }

    [Fact]
    public void Un_nom_buit_treu_el_programa()
    {
        Settings("""{ "v": 1, "id": 1, "cmd": "settings.set", "program": { "name": "Esports" } }""");
        var settings = Settings("""{ "v": 1, "id": 2, "cmd": "settings.set", "program": { "name": "" } }""");

        Assert.Equal("", settings["program"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Un_nom_massa_llarg_dona_bad_params()
    {
        string name = new('a', EngineConfig.MaxProgramName + 1);
        var r = _router.Handle($$"""{ "v": 1, "id": 1, "cmd": "settings.set", "program": { "name": "{{name}}" } }""");

        Assert.Equal("bad_params", ErrorCode(r));
    }

    [Fact]
    public void Canviar_el_programa_no_toca_la_resta()
    {
        Settings("""{ "v": 1, "id": 1, "cmd": "settings.set", "ui": { "zoom": 1.25 } }""");
        var settings = Settings("""{ "v": 1, "id": 2, "cmd": "settings.set", "program": { "name": "Esports" } }""");

        Assert.Equal(1.25, settings["ui"]!["zoom"]!.GetValue<double>());
    }
}
