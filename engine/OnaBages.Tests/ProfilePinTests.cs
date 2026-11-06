using System.IO.Compression;
using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>
/// PIN de cada perfil (fase 5, pas 13a): profile.setPin, profile.checkPin i hasPin a profile.list.
/// El motor no dona mai el PIN; l'exportació no l'inclou.
/// </summary>
public class ProfilePinTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "perfils-pin-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigRepository _repository;
    private readonly ConfigStore _store;
    private readonly EngineState _state;
    private readonly CommandRouter _router;

    public ProfilePinTests()
    {
        Directory.CreateDirectory(_folder);
        _repository = new ConfigRepository(Path.Combine(_folder, "config.json"));
        _store = new ConfigStore(_repository);
        _store.Load();
        _state = new EngineState([new Player("1")], [new Recorder("1", _ => new FakeInput())]);
        _router = new CommandRouter(_state, () => { }, () => [], config: _store);
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

    private static string Json(string path) => path.Replace("\\", "\\\\");

    private JsonObject SetPin(string profile, string pinJson) =>
        Send($$"""{ "v": 1, "id": 1, "cmd": "profile.setPin", "profile": "{{profile}}", "pin": {{pinJson}} }""");

    private bool Check(string profile, string pin) =>
        Ok($$"""{ "v": 1, "id": 1, "cmd": "profile.checkPin", "profile": "{{profile}}", "pin": "{{pin}}" }""")["valid"]!.GetValue<bool>();

    private bool HasPin(string profile) => Ok("""{ "v": 1, "id": 1, "cmd": "profile.list" }""")["profiles"]!.AsArray()
        .Single(p => p!["id"]!.GetValue<string>() == profile)!["hasPin"]!.GetValue<bool>();

    private string ProfileFileText(string id) => File.ReadAllText(_repository.ProfileFilePath(id));

    private void CreateProfile(string name, string? copyFrom = null) => Ok(copyFrom is null
        ? $$"""{ "v": 1, "id": 1, "cmd": "profile.create", "name": "{{name}}" }"""
        : $$"""{ "v": 1, "id": 1, "cmd": "profile.create", "name": "{{name}}", "copyFrom": "{{copyFrom}}" }""");

    // --- Sense PIN ---

    [Fact]
    public void Sense_PIN_qualsevol_PIN_es_valid()
    {
        Assert.False(HasPin("Principal"));
        Assert.True(Check("Principal", "0000"));
        Assert.True(Check("Principal", ""));
    }

    // --- Perfil actiu ---

    [Fact]
    public void El_PIN_del_perfil_actiu_es_desa_i_es_comprova()
    {
        Assert.True(SetPin("Principal", "\"2468\"")["ok"]!.GetValue<bool>());

        Assert.True(HasPin("Principal"));
        Assert.True(Check("Principal", "2468"));
        Assert.False(Check("Principal", "2469"));
        Assert.False(Check("Principal", ""));
        Assert.Contains("\"pin\": \"2468\"", ProfileFileText("Principal"));

        // En tornar a arrencar, el motor el llegeix.
        Assert.Equal("2468", new ConfigRepository(_repository.FilePath).Load().Config.ProfilePin);
    }

    [Fact]
    public void Altres_canvis_de_configuracio_no_esborren_el_PIN()
    {
        SetPin("Principal", "\"2468\"");

        _store.Update(c => c.ProgramName = "Matins");

        Assert.Contains("\"pin\": \"2468\"", ProfileFileText("Principal"));
        Assert.True(HasPin("Principal"));
    }

    [Fact]
    public void El_motor_no_dona_mai_el_PIN()
    {
        SetPin("Principal", "\"2468\"");

        foreach (string cmd in new[] { "profile.list", "settings.get", "state.get" })
        {
            var r = Send($$"""{ "v": 1, "id": 1, "cmd": "{{cmd}}" }""");
            Assert.DoesNotContain("2468", r.ToJsonString());
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    public void El_PIN_es_pot_treure(string pinJson)
    {
        SetPin("Principal", "\"2468\"");

        Assert.True(SetPin("Principal", pinJson)["ok"]!.GetValue<bool>());

        Assert.False(HasPin("Principal"));
        Assert.True(Check("Principal", "0000"));
        Assert.DoesNotContain("\"pin\"", ProfileFileText("Principal"));
    }

    [Theory]
    [InlineData("\"123\"")]        // massa curt
    [InlineData("\"123456789\"")]  // massa llarg
    [InlineData("\"12a4\"")]       // no són xifres
    [InlineData("\" 1234\"")]
    [InlineData("1234")]           // un número, no un text
    public void Un_PIN_no_valid_dona_bad_params_i_no_canvia_res(string pinJson)
    {
        SetPin("Principal", "\"2468\"");

        Assert.Equal("bad_params", ErrorCode(SetPin("Principal", pinJson)));

        Assert.True(Check("Principal", "2468"));
    }

    [Fact]
    public void Ordres_amb_parametres_que_falten()
    {
        Assert.Equal("bad_params", ErrorCode(Send("""{ "v": 1, "id": 1, "cmd": "profile.setPin", "profile": "Principal" }""")));
        Assert.Equal("bad_params", ErrorCode(Send("""{ "v": 1, "id": 1, "cmd": "profile.checkPin", "profile": "Principal" }""")));
        Assert.Equal("bad_params", ErrorCode(Send("""{ "v": 1, "id": 1, "cmd": "profile.checkPin", "profile": "No hi és", "pin": "1234" }""")));
        Assert.Equal("bad_params", ErrorCode(SetPin("No hi és", "\"1234\"")));
    }

    // --- Altres perfils ---

    [Fact]
    public void El_PIN_dun_altre_perfil_es_desa_al_seu_fitxer()
    {
        CreateProfile("Esports");

        Assert.True(SetPin("Esports", "\"13579\"")["ok"]!.GetValue<bool>());

        Assert.True(HasPin("Esports"));
        Assert.False(HasPin("Principal"));
        Assert.True(Check("Esports", "13579"));
        Assert.False(Check("Esports", "2468"));
        Assert.Contains("\"pin\": \"13579\"", ProfileFileText("Esports"));
        Assert.DoesNotContain("\"pin\"", ProfileFileText("Principal"));

        SetPin("Esports", "null");
        Assert.False(HasPin("Esports"));
    }

    [Fact]
    public void Un_PIN_no_valid_escrit_a_ma_al_fitxer_no_compta()
    {
        CreateProfile("Esports");
        string path = _repository.ProfileFilePath("Esports");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["pin"] = "12";
        File.WriteAllText(path, root.ToJsonString());

        Assert.False(HasPin("Esports"));
        Assert.True(Check("Esports", "0000"));
    }

    [Fact]
    public void Un_perfil_nou_no_te_PIN_i_un_duplicat_conserva_el_de_lorigen()
    {
        SetPin("Principal", "\"2468\"");

        CreateProfile("Buit");
        CreateProfile("Còpia", copyFrom: "Principal");

        Assert.False(HasPin("Buit"));
        Assert.True(HasPin("Còpia"));
        Assert.True(Check("Còpia", "2468"));
    }

    // --- Exportar i importar ---

    [Fact]
    public void Lexportacio_no_porta_el_PIN_i_el_perfil_importat_no_en_te()
    {
        SetPin("Principal", "\"2468\"");
        string zip = Path.Combine(_folder, "perfil.zip");

        Ok($$"""{ "v": 1, "id": 1, "cmd": "profile.export", "profile": "Principal", "path": "{{Json(zip)}}" }""");

        using (var archive = ZipFile.OpenRead(zip))
        {
            using var reader = new StreamReader(archive.GetEntry("perfil.json")!.Open());
            string text = reader.ReadToEnd();
            Assert.DoesNotContain("pin", text);
            Assert.Contains("\"name\": \"Principal\"", text);
        }
        Assert.True(HasPin("Principal")); // l'original el conserva

        Ok($$"""{ "v": 1, "id": 1, "cmd": "profile.import", "path": "{{Json(zip)}}", "name": "Importat" }""");
        Assert.False(HasPin("Importat"));
    }

    [Fact]
    public void Un_zip_fet_a_ma_amb_PIN_simporta_sense()
    {
        SetPin("Principal", "\"2468\"");
        string zip = Path.Combine(_folder, "amb-pin.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var root = JsonNode.Parse(ProfileFileText("Principal"))!.AsObject();
            using var writer = new StreamWriter(archive.CreateEntry("perfil.json").Open());
            writer.Write(root.ToJsonString());
        }

        Ok($$"""{ "v": 1, "id": 1, "cmd": "profile.import", "path": "{{Json(zip)}}", "name": "Altre lloc" }""");

        Assert.False(HasPin("Altre lloc"));
        Assert.DoesNotContain("\"pin\"", ProfileFileText("Altre lloc"));
    }
}
