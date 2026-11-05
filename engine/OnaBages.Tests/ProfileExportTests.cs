using System.IO.Compression;
using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>Exportar i importar perfils en .zip (fase 4, pas 1c; D-061): profile.export i profile.import.</summary>
public class ProfileExportTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "perfils-zip-" + Guid.NewGuid().ToString("N"));

    /// <summary>Dos «ordinadors»: el de l'estudi exporta, el de casa importa.</summary>
    private readonly Computer _studio;
    private readonly Computer _home;

    public ProfileExportTests()
    {
        Directory.CreateDirectory(_folder);
        _studio = new Computer(Path.Combine(_folder, "estudi"));
        _home = new Computer(Path.Combine(_folder, "casa"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private sealed class Computer
    {
        public ConfigRepository Repository { get; }
        public ConfigStore Store { get; }
        public CommandRouter Router { get; }

        public Computer(string folder)
        {
            Directory.CreateDirectory(folder);
            Repository = new ConfigRepository(Path.Combine(folder, "config.json"));
            Store = new ConfigStore(Repository);
            Store.Load();
            Router = new CommandRouter(new EngineState([new Player("1")], [new Recorder("1")]), () => { }, () => [], config: Store);
        }

        public JsonObject Send(string json) => Router.Handle(json);

        public JsonObject Ok(string json)
        {
            var r = Send(json);
            Assert.True(r["ok"]!.GetValue<bool>(), r.ToJsonString());
            return r["result"]!.AsObject();
        }
    }

    private static string Json(string path) => path.Replace("\\", "\\\\");

    private static string ErrorCode(JsonObject r) => r["error"]!["code"]!.GetValue<string>();

    private string Zip(string name) => Path.Combine(_folder, name);

    private JsonObject Import(Computer computer, string zip, string? name = null) => computer.Send(name is null
        ? $$"""{ "v": 1, "id": 1, "cmd": "profile.import", "path": "{{Json(zip)}}" }"""
        : $$"""{ "v": 1, "id": 1, "cmd": "profile.import", "path": "{{Json(zip)}}", "name": "{{name}}" }""");

    /// <summary>Un .zip fet a mà, amb les entrades que es vulgui.</summary>
    private string MakeZip(string fileName, params (string Entry, string Content)[] entries)
    {
        string path = Zip(fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(content);
        }
        return path;
    }

    private const string ValidProfile = """{ "schemaVersion": 1, "name": "Esports", "layout": { "players": 3, "recorders": 1 } }""";

    // --- Exportar i importar ---

    [Fact]
    public void Un_perfil_exportat_es_pot_importar_en_un_altre_ordinador()
    {
        _studio.Store.Update(c =>
        {
            c.ProfileName = "La Central";
            c.PlayerCount = 4;
            c.Players["1"] = new DeviceRef("{sb}", "Sound Blaster");
            c.ProgramName = "La Central";
        });
        string lists = _studio.Repository.LastListsFolder("Principal");
        Directory.CreateDirectory(lists);
        File.WriteAllText(Path.Combine(lists, "reproductor-1.obpl"), """{ "schemaVersion": 1, "items": [] }""");
        string zip = Zip("La Central.zip");

        var exported = _studio.Ok($$"""{ "v": 1, "id": 1, "cmd": "profile.export", "profile": "Principal", "path": "{{Json(zip)}}" }""");
        var imported = Import(_home, zip);

        Assert.Equal("La Central", exported["profile"]!["name"]!.GetValue<string>());
        Assert.True(imported["ok"]!.GetValue<bool>(), imported.ToJsonString());
        var profile = imported["result"]!["profile"]!;
        Assert.Equal("La Central", profile["id"]!.GetValue<string>());
        Assert.Equal("La Central", profile["name"]!.GetValue<string>());

        // Es llegeix igual que a l'estudi (mirant-lo com si fos l'actiu).
        _home.Repository.Save(WithActive(_home.Repository, "La Central"));
        var loaded = _home.Repository.Load().Config;
        Assert.Equal(4, loaded.PlayerCount);
        Assert.Equal("{sb}", loaded.Players["1"].Id);
        Assert.True(File.Exists(Path.Combine(_home.Repository.LastListsFolder("La Central"), "reproductor-1.obpl")));
    }

    /// <summary>config.json que apunta a un altre perfil, sense tocar-ne cap.</summary>
    private static EngineConfig WithActive(ConfigRepository repository, string id)
    {
        File.WriteAllText(repository.FilePath, $$"""{ "schemaVersion": 2, "activeProfile": "{{id}}" }""");
        return repository.Load().Config;
    }

    [Fact]
    public void Importar_no_canvia_el_perfil_actiu()
    {
        string zip = MakeZip("esports.zip", ("perfil.json", ValidProfile));

        Import(_home, zip);

        var profiles = _home.Ok("""{ "v": 1, "id": 2, "cmd": "profile.list" }""")["profiles"]!.AsArray();
        Assert.Equal(2, profiles.Count);
        Assert.True(profiles.Single(p => p!["id"]!.GetValue<string>() == "Principal")!["active"]!.GetValue<bool>());
        Assert.Equal("Principal", _home.Repository.Load().Config.ProfileId);
    }

    [Fact]
    public void Si_ja_hi_ha_un_perfil_amb_el_mateix_nom_dona_profile_exists_i_es_pot_triar_un_altre_nom()
    {
        string zip = MakeZip("esports.zip", ("perfil.json", ValidProfile));
        Import(_home, zip);

        var again = Import(_home, zip);
        var renamed = Import(_home, zip, "Esports de casa");

        Assert.Equal("profile_exists", ErrorCode(again));
        Assert.True(renamed["ok"]!.GetValue<bool>(), renamed.ToJsonString());
        Assert.Equal("Esports de casa", renamed["result"]!["profile"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Exportar_substitueix_un_fitxer_que_ja_hi_era()
    {
        string zip = Zip("perfil.zip");
        File.WriteAllText(zip, "antic");

        _studio.Ok($$"""{ "v": 1, "id": 1, "cmd": "profile.export", "profile": "Principal", "path": "{{Json(zip)}}" }""");

        using var archive = ZipFile.OpenRead(zip);
        Assert.NotNull(archive.GetEntry("perfil.json"));
        Assert.False(File.Exists(zip + ".tmp"));
    }

    // --- Fitxers que no són perfils, o malintencionats ---

    [Fact]
    public void Un_zip_sense_perfil_json_no_es_importa()
    {
        string zip = MakeZip("fotos.zip", ("foto.jpg", "..."));

        var r = Import(_home, zip);

        Assert.Equal("file_error", ErrorCode(r));
        Assert.Single(_home.Repository.ListProfiles());
    }

    [Fact]
    public void Un_fitxer_que_no_es_zip_dona_file_error()
    {
        string path = Zip("no-es-zip.zip");
        File.WriteAllText(path, "hola");

        Assert.Equal("file_error", ErrorCode(Import(_home, path)));
    }

    [Fact]
    public void Un_perfil_duna_versio_desconeguda_no_es_importa()
    {
        string zip = MakeZip("futur.zip", ("perfil.json", """{ "schemaVersion": 9, "name": "Futur" }"""));

        var r = Import(_home, zip);

        Assert.Equal("file_error", ErrorCode(r));
        Assert.Contains("9", r["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void Les_entrades_amb_camins_no_surten_de_la_carpeta_del_perfil()
    {
        string zip = MakeZip("perill.zip",
            ("perfil.json", ValidProfile),
            ("llistes/../../../fora.obpl", "{}"),
            ("../fora2.obpl", "{}"),
            ("llistes/sub/dins.obpl", "{}"),
            ("llistes/virus.exe", "MZ"),
            ("llistes/bona.obpl", "{}"));

        var r = Import(_home, zip);

        Assert.True(r["ok"]!.GetValue<bool>(), r.ToJsonString());
        string profile = _home.Repository.ProfileFolder("Esports");
        Assert.Equal(["bona.obpl"], Directory.GetFiles(Path.Combine(profile, "llistes")).Select(Path.GetFileName));
        Assert.Equal(["perfil.json"], Directory.GetFiles(profile).Select(Path.GetFileName));
        Assert.Empty(Directory.GetFiles(_folder, "fora*.obpl", SearchOption.AllDirectories));
    }

    [Fact]
    public void Un_fitxer_massa_gran_dins_del_zip_no_simporta()
    {
        string zip = MakeZip("gran.zip",
            ("perfil.json", ValidProfile),
            ("llistes/enorme.obpl", new string(' ', (int)ConfigRepository.MaxImportEntryBytes + 1)));

        var r = Import(_home, zip);

        Assert.Equal("file_error", ErrorCode(r));
        Assert.Single(_home.Repository.ListProfiles());
    }

    [Fact]
    public void Una_importacio_fallida_no_deixa_carpetes_temporals()
    {
        Import(_home, MakeZip("futur.zip", ("perfil.json", """{ "schemaVersion": 9 }""")));

        Assert.Empty(Directory.GetDirectories(_home.Repository.ProfilesFolder, ".importacio-*"));
    }

    [Fact]
    public void Les_rutes_han_de_ser_completes()
    {
        var r = _home.Send("""{ "v": 1, "id": 1, "cmd": "profile.import", "path": "perfil.zip" }""");

        Assert.Equal("bad_params", ErrorCode(r));
    }

    [Fact]
    public void Exportar_un_perfil_que_no_existeix_dona_bad_params()
    {
        var r = _studio.Send($$"""{ "v": 1, "id": 1, "cmd": "profile.export", "profile": "No hi és", "path": "{{Json(Zip("x.zip"))}}" }""");

        Assert.Equal("bad_params", ErrorCode(r));
        Assert.False(File.Exists(Zip("x.zip")));
    }
}
