using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>
/// Historial d'enregistraments (fase 5, pas 10a; D-086): una còpia de cada original, escrita alhora que el
/// fitxer de treball. La còpia mai no pot afectar l'enregistrament, i la neteja només esborra el que toca.
/// </summary>
public class RecordingArchiveTests : IDisposable
{
    private const string DeviceId = "{0.0.1.00000000}.{proves}";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "arxiu-" + Guid.NewGuid().ToString("N"));
    private FakeInput? _input;

    public RecordingArchiveTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private string Work => Path.Combine(_folder, "treball");
    private string Archive => Path.Combine(_folder, "historial");

    private Recorder NewRecorder(string? archive)
    {
        var recorder = new Recorder("1", _ => _input = new FakeInput(48000, 2));
        recorder.SetDevice(DeviceId);
        recorder.SetArchive(archive);
        return recorder;
    }

    [Fact]
    public void La_copia_es_identica_i_va_a_la_carpeta_del_dia()
    {
        using var recorder = NewRecorder(Archive);
        recorder.Start(Work, "Programa");
        _input!.PushTone(3.0);
        recorder.Stop();

        string copy = recorder.ArchivePath!;
        Assert.Equal(Path.Combine(Archive, DateTime.Now.ToString("yyyy-MM-dd"), Path.GetFileName(recorder.FilePath!)), copy);
        Assert.Equal(File.ReadAllBytes(recorder.FilePath!), File.ReadAllBytes(copy));
        Assert.True(new FileInfo(copy).Length > 100_000);
        Assert.Null(recorder.ArchiveError);
        Assert.False(File.Exists(copy + ".rec"), "La còpia no porta marcador: no és un enregistrament per recuperar");
    }

    [Fact]
    public void Descartar_o_moure_l_original_no_toca_la_copia()
    {
        using var recorder = NewRecorder(Archive);
        recorder.Start(Work, "Programa");
        _input!.PushTone(1.0);
        recorder.Stop();
        byte[] original = File.ReadAllBytes(recorder.FilePath!);

        File.Delete(recorder.FilePath!); // com si s'hagués descartat i buidat la paperera
        recorder.Release(recorder.FilePath!);

        Assert.Equal(original, File.ReadAllBytes(recorder.ArchivePath!));
    }

    [Fact]
    public void Dos_enregistraments_amb_el_mateix_nom_no_se_sobreescriuen()
    {
        using var recorder = NewRecorder(Archive);
        recorder.Start(Work, "Programa");
        _input!.PushTone(0.5);
        recorder.Stop();
        string first = recorder.ArchivePath!;
        recorder.Start(Work, "Programa"); // pot ser el mateix segon
        _input.PushTone(0.5);
        recorder.Stop();

        Assert.NotEqual(first, recorder.ArchivePath);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(recorder.ArchivePath));
    }

    [Fact]
    public void Sense_historial_no_hi_ha_copia()
    {
        using var recorder = NewRecorder(archive: null);
        recorder.Start(Work, "Programa");
        _input!.PushTone(0.5);
        recorder.Stop();

        Assert.Null(recorder.ArchivePath);
        Assert.Null(recorder.ArchiveError);
        Assert.False(Directory.Exists(Archive));
    }

    [Fact]
    public void Si_la_copia_no_es_pot_crear_l_enregistrament_continua_igual()
    {
        // La «carpeta» de l'historial és un fitxer: no s'hi pot crear res.
        File.WriteAllText(Archive, "no és una carpeta");
        using var recorder = NewRecorder(Archive);

        recorder.Start(Work, "Programa");
        _input!.PushTone(2.0);
        recorder.Stop();

        Assert.Equal(RecorderState.Pending, recorder.State);
        Assert.Equal(0, recorder.SamplesLost);
        Assert.True(new FileInfo(recorder.FilePath!).Length > 50_000);
        Assert.Null(recorder.ArchivePath);
        Assert.Contains("còpia d'historial", recorder.ArchiveError);
        Assert.Contains("continua", recorder.ArchiveError);
        Assert.StartsWith(Archive, recorder.ArchiveErrorDetail);
    }

    [Fact]
    public void La_carpeta_ha_de_ser_una_ruta_completa()
    {
        using var recorder = NewRecorder(archive: null);
        Assert.Throws<RecorderCommandException>(() => recorder.SetArchive("Historial"));
    }

    // --- Neteja dels dies antics ---

    [Fact]
    public void La_neteja_nomes_esborra_els_mp3_dels_dies_antics()
    {
        var today = new DateTime(2026, 11, 6);
        string Day(string name, params string[] files)
        {
            string dir = Path.Combine(Archive, name);
            Directory.CreateDirectory(dir);
            foreach (var f in files) File.WriteAllText(Path.Combine(dir, f), "x");
            return dir;
        }
        string old = Day("2026-09-01", "a.mp3", "b.mp3");
        string oldWithOther = Day("2026-10-01", "c.mp3", "notes.txt");
        string limit = Day("2026-10-07", "d.mp3");      // fa 30 dies just: es guarda
        string recent = Day("2026-11-06", "e.mp3");     // avui
        string other = Day("Altres", "f.mp3");           // no és un dia: no es toca

        int deleted = RecordingArchive.DeleteOld(Archive, 30, today);

        Assert.Equal(3, deleted);
        Assert.False(Directory.Exists(old));                              // buida: s'esborra
        Assert.True(File.Exists(Path.Combine(oldWithOther, "notes.txt"))); // el que no és MP3 es queda
        Assert.False(File.Exists(Path.Combine(oldWithOther, "c.mp3")));
        Assert.True(File.Exists(Path.Combine(limit, "d.mp3")));
        Assert.True(File.Exists(Path.Combine(recent, "e.mp3")));
        Assert.True(File.Exists(Path.Combine(other, "f.mp3")));
    }

    [Fact]
    public void La_neteja_tambe_mira_dins_de_cada_perfil()
    {
        var today = new DateTime(2026, 11, 6);
        string Dir(params string[] parts)
        {
            string dir = Path.Combine([Archive, .. parts]);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "a.mp3"), "x");
            return dir;
        }
        string oldA = Dir("La Central", "2026-09-01");
        string newA = Dir("La Central", "2026-11-06");
        string oldB = Dir("Magazín", "2026-08-01");
        string deeper = Dir("Magazín", "Altres", "2020-01-01"); // massa endins: no es toca

        Assert.Equal(2, RecordingArchive.DeleteOld(Archive, 30, today));
        Assert.False(Directory.Exists(oldA));
        Assert.False(Directory.Exists(oldB));
        Assert.True(Directory.Exists(Path.Combine(Archive, "La Central")));   // la del perfil es queda
        Assert.True(File.Exists(Path.Combine(newA, "a.mp3")));
        Assert.True(File.Exists(Path.Combine(deeper, "a.mp3")));
    }

    [Theory]
    [InlineData("La Central", "La Central")]
    [InlineData("Matí: 8/9 h", "Matí_ 8_9 h")]
    [InlineData("  Nit.  ", "Nit")]
    [InlineData("???", "___")]
    public void La_carpeta_del_perfil_porta_el_seu_nom(string profile, string folder)
    {
        Assert.Equal(Path.Combine(Archive, folder), RecordingArchive.ProfileFolder(Archive, profile));
    }

    [Fact]
    public void Amb_0_dies_no_s_esborra_mai_res()
    {
        string dir = Path.Combine(Archive, "2020-01-01");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.mp3"), "x");

        Assert.Equal(0, RecordingArchive.DeleteOld(Archive, 0, DateTime.Today));
        Assert.True(File.Exists(Path.Combine(dir, "a.mp3")));
    }

    [Fact]
    public void Un_fitxer_obert_no_s_esborra()
    {
        string dir = Path.Combine(Archive, "2020-01-01");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "obert.mp3");
        using var open = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);

        Assert.Equal(0, RecordingArchive.DeleteOld(Archive, 30, DateTime.Today));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Una_carpeta_que_no_existeix_no_es_cap_error()
    {
        Assert.Equal(0, RecordingArchive.DeleteOld(Path.Combine(_folder, "no-hi-és"), 30, DateTime.Today));
    }

    // --- Ajustos ---

    [Fact]
    public void Ajustos_per_defecte_ordre_i_config()
    {
        var repository = new ConfigRepository(Path.Combine(_folder, "dades", "config.json"));
        var store = new ConfigStore(repository);
        store.Load();
        using var recorder = NewRecorder(archive: null);
        var router = new CommandRouter(new EngineState([], [recorder]), () => { }, () => [], config: store);

        var settings = router.Handle("""{ "v": 1, "id": 1, "cmd": "settings.get" }""")["result"]!["settings"]!["recordingArchive"]!;
        Assert.True(settings["enabled"]!.GetValue<bool>());
        Assert.Equal(30, settings["keepDays"]!.GetValue<int>());
        Assert.Equal(RecordingArchive.DefaultFolder, settings["folder"]!.GetValue<string>());

        string folder = Archive.Replace("\\", "\\\\");
        var r = router.Handle($$"""{ "v": 1, "id": 2, "cmd": "settings.set", "recordingArchive": { "folder": "{{folder}}", "keepDays": 0 } }""");
        Assert.Equal(Archive, r["result"]!["settings"]!["recordingArchive"]!["folder"]!.GetValue<string>());
        string profile = store.Read(c => c.ProfileName);
        Assert.Equal(Path.Combine(Archive, profile), recorder.ArchiveFolder);       // subcarpeta del perfil (D-088)
        var saved = repository.Load().Config;
        Assert.Equal(Archive, saved.RecordingArchiveFolder);
        Assert.Equal(0, saved.RecordingArchiveKeepDays);
        Assert.Contains("recordingArchive", File.ReadAllText(Path.Combine(_folder, "dades", "config.json"))); // de l'ordinador

        router.Handle("""{ "v": 1, "id": 3, "cmd": "settings.set", "recordingArchive": { "enabled": false } }""");
        Assert.Null(recorder.ArchiveFolder);
        router.Handle("""{ "v": 1, "id": 4, "cmd": "settings.set", "recordingArchive": { "enabled": true, "folder": "" } }""");
        Assert.Equal(Path.Combine(RecordingArchive.DefaultFolder, profile), recorder.ArchiveFolder);

        // Canviar el nom del perfil actiu: les còpies van a la carpeta amb el nom nou.
        string id = store.Read(c => c.ProfileId);
        var renamed = router.Handle($$"""{ "v": 1, "id": 7, "cmd": "profile.rename", "profile": "{{id}}", "name": "La Central" }""");
        Assert.True(renamed["ok"]!.GetValue<bool>(), renamed.ToJsonString());
        Assert.Equal(Path.Combine(RecordingArchive.DefaultFolder, "La Central"), recorder.ArchiveFolder);

        Assert.Equal("bad_params", router.Handle("""{ "v": 1, "id": 5, "cmd": "settings.set", "recordingArchive": { "folder": "Historial" } }""")["error"]!["code"]!.GetValue<string>());
        Assert.Equal("bad_params", router.Handle("""{ "v": 1, "id": 6, "cmd": "settings.set", "recordingArchive": { "keepDays": -1 } }""")["error"]!["code"]!.GetValue<string>());

        var json = RecorderJson.Recorder(recorder);
        Assert.NotNull(json["archive"]);
    }
}
