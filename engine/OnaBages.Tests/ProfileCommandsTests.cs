using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>Ordres dels perfils (fase 4, pas 1b; D-058): profile.list, create, rename, delete i switch.</summary>
public class ProfileCommandsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "perfils-ordres-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigRepository _repository;
    private readonly ConfigStore _store;
    private readonly EngineState _state;
    private readonly CommandRouter _router;
    private volatile bool _shutdownRequested;

    public ProfileCommandsTests()
    {
        Directory.CreateDirectory(_folder);
        _repository = new ConfigRepository(Path.Combine(_folder, "config.json"));
        _store = new ConfigStore(_repository);
        _store.Load();
        _state = new EngineState([new Player("1"), new Player("2")], [new Recorder("1", _ => new FakeInput())]);
        _router = new CommandRouter(_state, () => _shutdownRequested = true, () => [], config: _store);
    }

    public void Dispose()
    {
        foreach (var r in _state.Recorders) r.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private JsonObject Send(string json) => _router.Handle(json);

    private JsonObject Ok(string json)
    {
        var r = Send(json);
        Assert.True(r["ok"]!.GetValue<bool>(), r.ToJsonString());
        return r["result"]!.AsObject();
    }

    private static string ErrorCode(JsonObject r) => r["error"]!["code"]!.GetValue<string>();

    private static List<(string Id, string Name, bool Active)> Profiles(JsonObject result) =>
        result["profiles"]!.AsArray()
            .Select(p => (p!["id"]!.GetValue<string>(), p["name"]!.GetValue<string>(), p["active"]!.GetValue<bool>()))
            .ToList();

    private JsonObject Create(string name, string? copyFrom = null) => Ok(copyFrom is null
        ? $$"""{ "v": 1, "id": 1, "cmd": "profile.create", "name": "{{name}}" }"""
        : $$"""{ "v": 1, "id": 1, "cmd": "profile.create", "name": "{{name}}", "copyFrom": "{{copyFrom}}" }""");

    // --- Llistar i crear ---

    [Fact]
    public void Al_principi_hi_ha_un_sol_perfil_i_es_lactiu()
    {
        var profiles = Profiles(Ok("""{ "v": 1, "id": 1, "cmd": "profile.list" }"""));

        Assert.Equal([("Principal", "Principal", true)], profiles);
    }

    [Fact]
    public void Un_perfil_nou_es_buit_i_porta_el_programa_del_mateix_nom()
    {
        _store.Update(c => c.Players["1"] = new DeviceRef("{sb}", "Sound Blaster"));

        var result = Create("Esports");

        Assert.Equal("Esports", result["profile"]!["id"]!.GetValue<string>());
        Assert.Contains(("Esports", "Esports", false), Profiles(result));
        var loaded = LoadProfile("Esports");
        Assert.Empty(loaded.Players);
        Assert.Equal("Esports", loaded.ProgramName);
        Assert.Equal(2, loaded.PlayerCount);
    }

    [Fact]
    public void Duplicar_copia_la_configuracio_i_les_ultimes_llistes()
    {
        _store.Update(c =>
        {
            c.Players["1"] = new DeviceRef("{sb}", "Sound Blaster");
            c.PlayerCount = 4;
            c.ProgramName = "La Central";
        });
        string lists = _repository.LastListsFolder("Principal");
        Directory.CreateDirectory(lists);
        File.WriteAllText(Path.Combine(lists, "reproductor-1.obpl"), "{}");

        Create("La Central dissabtes", copyFrom: "Principal");

        var copy = LoadProfile("La Central dissabtes");
        Assert.Equal("La Central dissabtes", copy.ProfileName);
        Assert.Equal("{sb}", copy.Players["1"].Id);
        Assert.Equal(4, copy.PlayerCount);
        Assert.Equal("La Central", copy.ProgramName); // el programa es copia tal com era
        Assert.True(File.Exists(Path.Combine(_repository.LastListsFolder("La Central dissabtes"), "reproductor-1.obpl")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("principal")] // ja n'hi ha un que es diu així (no es distingeixen majúscules)
    public void Noms_no_valids_donen_bad_params(string name)
    {
        var r = Send($$"""{ "v": 1, "id": 1, "cmd": "profile.create", "name": "{{name}}" }""");

        Assert.Equal("bad_params", ErrorCode(r));
    }

    [Fact]
    public void Copiar_un_perfil_que_no_existeix_dona_bad_params()
    {
        var r = Send("""{ "v": 1, "id": 1, "cmd": "profile.create", "name": "Nou", "copyFrom": "No hi és" }""");

        Assert.Equal("bad_params", ErrorCode(r));
        Assert.False(Directory.Exists(_repository.ProfileFolder("Nou")));
    }

    // --- Canviar el nom ---

    [Fact]
    public void Canviar_el_nom_dun_perfil_que_no_es_lactiu()
    {
        Create("Esports");

        var result = Ok("""{ "v": 1, "id": 2, "cmd": "profile.rename", "profile": "Esports", "name": "Esports i més" }""");

        Assert.Contains(("Esports", "Esports i més", false), Profiles(result));
        Assert.True(Directory.Exists(_repository.ProfileFolder("Esports"))); // la carpeta no canvia
    }

    [Fact]
    public void Canviar_el_nom_del_perfil_actiu_es_desa_i_surt_als_ajustos()
    {
        var result = Ok("""{ "v": 1, "id": 1, "cmd": "profile.rename", "profile": "Principal", "name": "La Central" }""");

        Assert.Equal("La Central", result["settings"]!["profile"]!["name"]!.GetValue<string>());
        Assert.Equal("La Central", _repository.Load().Config.ProfileName);
    }

    [Fact]
    public void No_es_pot_posar_el_nom_dun_altre_perfil()
    {
        Create("Esports");

        var r = Send("""{ "v": 1, "id": 2, "cmd": "profile.rename", "profile": "Principal", "name": "ESPORTS" }""");

        Assert.Equal("bad_params", ErrorCode(r));
    }

    [Fact]
    public void Es_pot_canviar_nomes_les_majuscules_del_mateix_perfil()
    {
        var result = Ok("""{ "v": 1, "id": 1, "cmd": "profile.rename", "profile": "Principal", "name": "PRINCIPAL" }""");

        Assert.Contains(("Principal", "PRINCIPAL", true), Profiles(result));
    }

    // --- Eliminar ---

    [Fact]
    public void Eliminar_un_perfil_esborra_la_seva_carpeta()
    {
        Create("Esports");

        var result = Ok("""{ "v": 1, "id": 2, "cmd": "profile.delete", "profile": "Esports" }""");

        Assert.DoesNotContain(Profiles(result), p => p.Id == "Esports");
        Assert.False(Directory.Exists(_repository.ProfileFolder("Esports")));
    }

    [Fact]
    public void El_perfil_actiu_no_es_pot_eliminar()
    {
        var r = Send("""{ "v": 1, "id": 1, "cmd": "profile.delete", "profile": "Principal" }""");

        Assert.Equal("bad_state", ErrorCode(r));
        Assert.True(File.Exists(_repository.ProfileFilePath("Principal")));
    }

    [Fact]
    public void Eliminar_un_perfil_que_no_existeix_dona_bad_params()
    {
        var r = Send("""{ "v": 1, "id": 1, "cmd": "profile.delete", "profile": "..\\.." }""");

        Assert.Equal("bad_params", ErrorCode(r));
    }

    // --- Canviar de perfil ---

    [Fact]
    public void Canviar_de_perfil_el_deixa_triat_i_atura_el_motor()
    {
        _store.Update(c => c.PlayerCount = 4);
        Create("Esports");

        var result = Ok("""{ "v": 1, "id": 2, "cmd": "profile.switch", "profile": "Esports" }""");

        Assert.True(result["switching"]!.GetValue<bool>());
        Assert.Equal("Esports", result["profile"]!["id"]!.GetValue<string>());
        Assert.True(WaitFor(() => _shutdownRequested));
        // En tornar a arrencar, el motor llegirà el perfil nou.
        var next = _repository.Load().Config;
        Assert.Equal("Esports", next.ProfileId);
        Assert.Equal(2, next.PlayerCount);
        // I el perfil d'abans conserva el que tenia.
        Assert.Equal(4, LoadProfile("Principal").PlayerCount);
    }

    [Fact]
    public void Despres_de_canviar_de_perfil_no_es_desa_res_mes()
    {
        Create("Esports");
        Ok("""{ "v": 1, "id": 2, "cmd": "profile.switch", "profile": "Esports" }""");

        // Un canvi que arribi mentre el motor s'atura (p. ex. un dispositiu) no pot tornar a triar l'altre perfil.
        _store.Update(c => c.UiZoom = 1.25);

        Assert.Equal("Esports", _repository.Load().Config.ProfileId);
    }

    [Fact]
    public void No_es_pot_canviar_al_perfil_actiu()
    {
        var r = Send("""{ "v": 1, "id": 1, "cmd": "profile.switch", "profile": "Principal" }""");

        Assert.Equal("bad_params", ErrorCode(r));
        Assert.False(_shutdownRequested);
    }

    [Fact]
    public void Mentre_senregistra_no_es_pot_canviar_de_perfil()
    {
        Create("Esports");
        Send("""{ "v": 1, "id": 1, "cmd": "recorder.setDevice", "recorder": "1", "deviceId": "{fals}" }""");
        string folder = _folder.Replace("\\", "\\\\");
        Ok($$"""{ "v": 1, "id": 2, "cmd": "recorder.start", "recorder": "1", "folder": "{{folder}}" }""");

        var r = Send("""{ "v": 1, "id": 3, "cmd": "profile.switch", "profile": "Esports" }""");

        Assert.Equal("bad_state", ErrorCode(r));
        Assert.Contains("enregistrador 1", r["error"]!["message"]!.GetValue<string>());
        Assert.Equal("Principal", _repository.Load().Config.ProfileId);
        Thread.Sleep(400);
        Assert.False(_shutdownRequested);

        Send("""{ "v": 1, "id": 4, "cmd": "recorder.stop", "recorder": "1" }""");
    }

    // -----------------------------------------------------------------------

    /// <summary>Configuració d'un perfil tal com la llegiria el motor si fos l'actiu.</summary>
    private EngineConfig LoadProfile(string id)
    {
        string copy = Path.Combine(_folder, "lectura-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(copy, "perfils"));
        File.WriteAllText(Path.Combine(copy, "config.json"), $$"""{ "schemaVersion": 2, "activeProfile": "{{id}}" }""");
        var target = Path.Combine(copy, "perfils", id);
        Directory.CreateDirectory(target);
        File.Copy(_repository.ProfileFilePath(id), Path.Combine(target, ConfigRepository.ProfileFileName));
        return new ConfigRepository(Path.Combine(copy, "config.json")).Load().Config;
    }

    private static bool WaitFor(Func<bool> condition)
    {
        // Fins a 10 s: amb l'ordinador carregat (altres proves descodificant MP3), l'aturada pot trigar.
        for (int i = 0; i < 100 && !condition(); i++) Thread.Sleep(100);
        return condition();
    }
}
