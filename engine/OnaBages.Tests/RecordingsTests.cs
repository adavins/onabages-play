using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>
/// Pas 6a de la fase 2 (D-036): volum d'enregistrament, nom i carpeta de cada enregistrador,
/// Desar / Descartar i la llista d'enregistraments recents.
/// </summary>
public class RecordingsTests : IDisposable
{
    private const string DeviceId = "{0.0.1.00000000}.{proves}";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "recs-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _recycled = [];
    private FakeInput? _input;

    public RecordingsTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private Recorder NewRecorder(string id = "1")
    {
        var recorder = new Recorder(id, _ => _input = new FakeInput());
        recorder.SetDevice(DeviceId);
        recorder.Configure(folder: _folder);
        return recorder;
    }

    /// <summary>Llista amb paperera falsa (no omple la de debò) i desat sense fil a part.</summary>
    private RecordingList NewList(IEnumerable<Recorder> recorders) => new(
        isPending: path => recorders.Any(r => r.State == RecorderState.Pending && r.FilePath == path),
        sendToRecycleBin: path => { _recycled.Add(path); File.Delete(path); },
        synchronous: true);

    /// <summary>Enregistra un segon de to (0,3 de pic) i atura.</summary>
    private string RecordOneSecond(Recorder recorder)
    {
        recorder.Start();
        _input!.PushTone(1);
        recorder.Stop();
        return recorder.FilePath!;
    }

    private static float PeakOf(string mp3)
    {
        using var source = AudioFileSource.Open(mp3, 48000);
        return TestAudio.ReadAll(source).Select(Math.Abs).DefaultIfEmpty(0).Max();
    }

    // --- Volum d'enregistrament ---

    [Fact]
    public void El_volum_d_enregistrament_es_veu_al_mesurador()
    {
        using var recorder = NewRecorder();
        recorder.Meter.Take();

        recorder.SetGain(6);
        _input!.Push([0.25f, 0.25f, -0.25f, -0.25f]);
        Assert.Equal(0.5f, recorder.Meter.Take().Left, 0.01f);

        recorder.SetGain(-60); // al mínim: silenci
        _input.Push([0.25f, 0.25f]);
        Assert.Equal(0f, recorder.Meter.Take().Left);
    }

    [Fact]
    public void El_volum_queda_dins_del_marge_i_per_defecte_es_0_dB()
    {
        using var recorder = NewRecorder();
        Assert.Equal(0, recorder.GainDb);

        recorder.SetGain(20);
        Assert.Equal(Recorder.MaxGainDb, recorder.GainDb);
        recorder.SetGain(-200);
        Assert.Equal(Recorder.MinGainDb, recorder.GainDb);
    }

    [Fact]
    public void El_volum_d_enregistrament_s_aplica_al_fitxer()
    {
        using var recorder = NewRecorder();

        string normal = RecordOneSecond(recorder);
        recorder.SetGain(-60);
        string silent = RecordOneSecond(recorder);
        recorder.SetGain(12); // 0,3 × 4 = 1,2: es retalla a 1 (sense desbordar)
        string loud = RecordOneSecond(recorder);

        Assert.InRange(PeakOf(normal), 0.25f, 0.35f);
        Assert.InRange(PeakOf(silent), 0f, 0.01f);
        Assert.InRange(PeakOf(loud), 0.9f, 1.1f);
    }

    // --- Nom i carpeta ---

    [Fact]
    public void El_nom_i_la_carpeta_s_apliquen_al_proxim_enregistrament()
    {
        using var recorder = NewRecorder();
        string other = Path.Combine(_folder, "altra");

        recorder.Configure(name: "Programa", folder: other);
        string file = RecordOneSecond(recorder);

        Assert.Equal(other, Path.GetDirectoryName(file));
        Assert.StartsWith("Programa_", Path.GetFileName(file));

        recorder.Configure(name: "  ");
        Assert.Equal("Enregistrador 1", recorder.Name);
        Assert.Throws<RecorderCommandException>(() => recorder.Configure(folder: "relativa"));
    }

    [Fact]
    public void Cada_enregistrament_es_un_fitxer_nou_sense_sobreescriure_res()
    {
        using var recorder = NewRecorder();
        recorder.Configure(name: "Programa");

        // Dins del mateix segon: el segon fitxer porta _2.
        var files = new[] { RecordOneSecond(recorder), RecordOneSecond(recorder) };

        Assert.Equal(2, files.Distinct().Count());
        Assert.All(files, f => Assert.True(File.Exists(f)));
    }

    // --- Llista d'enregistraments ---

    [Fact]
    public void En_aturar_l_enregistrament_entra_a_la_llista()
    {
        using var recorder = NewRecorder();
        var list = NewList([recorder]);
        recorder.Finished += f => list.Add(f);

        string file = RecordOneSecond(recorder);

        var item = Assert.Single(list.Items);
        Assert.Equal(file, item.Path);
        Assert.Equal("1", item.RecorderId);
        Assert.True(item.ClosedCleanly);
        Assert.InRange(item.Duration.TotalSeconds, 0.9, 1.1);
        Assert.True(item.FileSize > 0);
    }

    [Fact]
    public void Desar_mou_el_fitxer_i_mai_no_sobreescriu()
    {
        using var recorder = NewRecorder();
        var list = NewList([recorder]);
        recorder.Finished += f => list.Add(f);
        string target = Path.Combine(_folder, "desats", "Entrevista.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, [1, 2, 3]); // ja n'hi ha un amb aquest nom

        string original = RecordOneSecond(recorder);
        string saved = list.Save(list.Items[0].Id, Path.Combine(_folder, "desats", "Entrevista"));

        Assert.Equal(Path.Combine(_folder, "desats", "Entrevista_2.mp3"), saved);
        Assert.True(File.Exists(saved));
        Assert.False(File.Exists(original));
        Assert.Equal(3, new FileInfo(target).Length); // l'altre no s'ha tocat
        Assert.True(list.Items[0].Saved);
        Assert.Equal(saved, list.Items[0].Path);
    }

    [Fact]
    public void L_enregistrament_pendent_no_es_pot_treure_ni_surt_de_la_llista()
    {
        using var recorder = NewRecorder();
        var list = NewList([recorder]);
        recorder.Finished += f => list.Add(f);
        RecordOneSecond(recorder);
        string pendingId = list.Items[0].Id;

        Assert.Throws<RecordingCommandException>(() => list.Remove(pendingId));

        // Encara que se n'hi afegeixin molts més, el pendent no en surt.
        for (int i = 0; i < RecordingList.MaxItems + 5; i++)
            list.Add(new FinishedRecording("2", Path.Combine(_folder, $"x{i}.mp3"), DateTimeOffset.Now, TimeSpan.FromSeconds(1), 1, true));

        Assert.Equal(RecordingList.MaxItems, list.Items.Count);
        Assert.NotNull(list.Find(pendingId));
    }

    // --- Ordres ---

    private (CommandRouter Router, EngineState State, RecordingList List) NewRouter()
    {
        var recorders = new[] { NewRecorder("1"), NewRecorder("2") };
        var list = NewList(recorders);
        foreach (var r in recorders) r.Finished += f => list.Add(f);
        var state = new EngineState([], recorders);
        return (new CommandRouter(state, () => { }, () => [], recordings: list), state, list);
    }

    private static string Escape(string path) => path.Replace("\\", "\\\\");

    [Fact]
    public void Ordres_configure_i_setGain()
    {
        var (router, state, _) = NewRouter();
        string folder = Escape(Path.Combine(_folder, "prog"));

        var r = router.Handle($$"""{ "v": 1, "id": 1, "cmd": "recorder.configure", "recorder": "1", "name": "Programa", "folder": "{{folder}}" }""");
        Assert.Equal("Programa", r["result"]!["recorder"]!["name"]!.GetValue<string>());

        r = router.Handle("""{ "v": 1, "id": 2, "cmd": "recorder.setGain", "recorder": "1", "db": 4.5 }""");
        Assert.Equal(4.5, r["result"]!["recorder"]!["gainDb"]!.GetValue<double>());

        Assert.Equal("bad_params", router.Handle("""{ "v": 1, "id": 3, "cmd": "recorder.configure", "recorder": "1", "folder": "rel" }""")["error"]!["code"]!.GetValue<string>());
        Assert.Equal("bad_params", router.Handle("""{ "v": 1, "id": 4, "cmd": "recorder.setGain", "recorder": "1" }""")["error"]!["code"]!.GetValue<string>());
        foreach (var rec in state.Recorders) rec.Dispose();
    }

    [Fact]
    public void Ordres_desar_i_descartar_tornen_l_enregistrador_a_inactiu()
    {
        var (router, state, list) = NewRouter();
        var one = state.FindRecorder("1")!;
        var two = state.FindRecorder("2")!;
        RecordOneSecond(one);
        string discardPath = RecordOneSecond(two);
        string idOne = list.Items.Single(i => i.RecorderId == "1").Id;
        string idTwo = list.Items.Single(i => i.RecorderId == "2").Id;

        var r = router.Handle($$"""{ "v": 1, "id": 1, "cmd": "recording.save", "recordingId": "{{idOne}}", "path": "{{Escape(Path.Combine(_folder, "Desat.mp3"))}}" }""");
        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.Equal(RecorderState.Idle, one.State);
        // L'enregistrador alliberat no arrossega dades de l'enregistrament anterior.
        Assert.Null(one.FilePath);
        Assert.Null(one.StartedAt);
        Assert.Equal(0, one.FileSize);
        Assert.Equal(TimeSpan.Zero, one.Elapsed);
        Assert.True(File.Exists(Path.Combine(_folder, "Desat.mp3")));

        router.Handle($$"""{ "v": 1, "id": 2, "cmd": "recording.discard", "recordingId": "{{idTwo}}" }""");
        Assert.Equal(RecorderState.Idle, two.State);
        Assert.Equal([discardPath], _recycled);
        Assert.Null(list.Find(idTwo));

        // El desat continua a la llista (recents); es pot treure sense tocar el fitxer.
        router.Handle($$"""{ "v": 1, "id": 3, "cmd": "recording.remove", "recordingId": "{{idOne}}" }""");
        Assert.Empty(list.Items);
        Assert.True(File.Exists(Path.Combine(_folder, "Desat.mp3")));

        var get = router.Handle("""{ "v": 1, "id": 4, "cmd": "state.get" }""");
        Assert.Empty(get["result"]!["recordings"]!.AsArray());
        Assert.Equal("bad_params", router.Handle("""{ "v": 1, "id": 5, "cmd": "recording.remove", "recordingId": "e99" }""")["error"]!["code"]!.GetValue<string>());
        foreach (var rec in state.Recorders) rec.Dispose();
    }

    [Fact]
    public void Començar_un_enregistrament_nou_deixa_l_anterior_a_recents()
    {
        var (router, state, list) = NewRouter();
        var one = state.FindRecorder("1")!;
        string first = RecordOneSecond(one);

        one.Start(); // des de Pendent, sense preguntar res
        _input!.PushTone(0.2);

        Assert.Equal(RecorderState.Recording, one.State);
        Assert.Equal(first, Assert.Single(list.Items).Path);
        Assert.Throws<RecorderCommandException>(() => one.Start()); // mentre enregistra, no se'n pot començar un altre
        one.Stop();
        Assert.Equal(2, list.Items.Count);
        foreach (var rec in state.Recorders) rec.Dispose();
    }

    // --- Desar la versió editada (fase 5, pas 14b; D-102) ---

    /// <summary>Simula l'editor: una còpia del fitxer a una altra carpeta, com si fos la versió editada.</summary>
    private string EditedCopy(string original, string name)
    {
        string folder = Path.Combine(_folder, "Desats");
        Directory.CreateDirectory(folder);
        string edited = Path.Combine(folder, name);
        File.Copy(original, edited);
        return edited;
    }

    [Fact]
    public void Desar_la_versio_editada_amb_historial_envia_l_original_a_la_paperera()
    {
        var (router, state, list) = NewRouter();
        var one = state.FindRecorder("1")!;
        string archive = Path.Combine(_folder, "historial");
        one.SetArchive(archive);
        string original = RecordOneSecond(one);
        var item = list.Items.Single();
        Assert.NotNull(item.ArchivePath); // la còpia d'historial es recorda
        string edited = EditedCopy(original, "Programa editat.mp3");

        var r = router.Handle($$"""{ "v": 1, "id": 1, "cmd": "recording.saveEdited", "recordingId": "{{item.Id}}", "path": "{{Escape(edited)}}" }""");

        Assert.True(r["ok"]!.GetValue<bool>(), r.ToJsonString());
        Assert.True(r["result"]!["original"]!["recycled"]!.GetValue<bool>());
        Assert.Null(r["result"]!["original"]!["kept"]);
        Assert.Equal(RecorderState.Idle, one.State); // ja no és pendent
        var saved = list.Find(item.Id)!;
        Assert.Equal(edited, saved.Path);
        Assert.True(saved.Saved);
        Assert.Equal(Mp3FrameIndex.Build(edited).Duration, saved.Duration); // llegida del fitxer desat
        Assert.Equal([original], _recycled);
        Assert.True(File.Exists(item.ArchivePath)); // la còpia de l'historial no es toca
        foreach (var rec in state.Recorders) rec.Dispose();
    }

    [Fact]
    public void Desar_la_versio_editada_sense_historial_deixa_l_original_on_es()
    {
        var (router, state, list) = NewRouter();
        var two = state.FindRecorder("2")!;
        string original = RecordOneSecond(two);
        var item = list.Items.Single();
        Assert.Null(item.ArchivePath);
        string edited = EditedCopy(original, "Línia editada.mp3");

        var r = router.Handle($$"""{ "v": 1, "id": 1, "cmd": "recording.saveEdited", "recordingId": "{{item.Id}}", "path": "{{Escape(edited)}}" }""");

        Assert.True(r["ok"]!.GetValue<bool>(), r.ToJsonString());
        Assert.False(r["result"]!["original"]!["recycled"]!.GetValue<bool>());
        Assert.Contains("historial", r["result"]!["original"]!["kept"]!.GetValue<string>());
        Assert.True(File.Exists(original)); // és l'únic original: es queda
        Assert.Empty(_recycled);
        Assert.Equal(RecorderState.Idle, two.State);
        Assert.True(list.Find(item.Id)!.Saved);
        foreach (var rec in state.Recorders) rec.Dispose();
    }

    [Fact]
    public void Desar_la_versio_editada_amb_errors_no_canvia_res()
    {
        var (router, state, list) = NewRouter();
        var one = state.FindRecorder("1")!;
        string original = RecordOneSecond(one);
        var item = list.Items.Single();

        string Code(string json) => router.Handle(json)["error"]!["code"]!.GetValue<string>();
        // El mateix fitxer, un que no hi és i una ruta que no és completa.
        Code($$"""{ "v": 1, "id": 1, "cmd": "recording.saveEdited", "recordingId": "{{item.Id}}", "path": "{{Escape(original)}}" }""");
        Code($$"""{ "v": 1, "id": 2, "cmd": "recording.saveEdited", "recordingId": "{{item.Id}}", "path": "{{Escape(Path.Combine(_folder, "no-hi-es.mp3"))}}" }""");
        Assert.Equal("bad_params", Code($$"""{ "v": 1, "id": 3, "cmd": "recording.saveEdited", "recordingId": "{{item.Id}}", "path": "editat.mp3" }"""));
        Assert.Equal("bad_params", Code("""{ "v": 1, "id": 4, "cmd": "recording.saveEdited", "recordingId": "e99", "path": "C:\\x.mp3" }"""));

        Assert.Equal(RecorderState.Pending, one.State);
        Assert.False(list.Find(item.Id)!.Saved);
        Assert.Equal(original, list.Find(item.Id)!.Path);
        Assert.Empty(_recycled);
        foreach (var rec in state.Recorders) rec.Dispose();
    }

    // --- Persistència ---

    [Fact]
    public void La_llista_de_recents_es_desa_i_es_recupera()
    {
        string file = Path.Combine(_folder, "recents.json");
        var list = new RecordingList(synchronous: true);
        list.Add(new FinishedRecording("1", @"C:\a\Programa.mp3", DateTimeOffset.Parse("2026-11-04T10:00:00+01:00"), TimeSpan.FromMinutes(62), 1234, true));
        list.Add(new FinishedRecording("2", @"C:\a\Línia.mp3", DateTimeOffset.Parse("2026-11-04T11:00:00+01:00"), TimeSpan.FromSeconds(5), 99, false,
            ArchivePath: @"C:\h\2026-11-04\Línia.mp3"));

        RecentRecordingsFile.Write(list.Items, file);
        var loaded = new RecordingList(synchronous: true);
        loaded.AddLoaded(RecentRecordingsFile.Read(file));

        Assert.Equal([@"C:\a\Línia.mp3", @"C:\a\Programa.mp3"], loaded.Items.Select(i => i.Path));
        Assert.False(loaded.Items[0].ClosedCleanly);
        Assert.Equal(TimeSpan.FromMinutes(62), loaded.Items[1].Duration);
        Assert.Equal(@"C:\h\2026-11-04\Línia.mp3", loaded.Items[0].ArchivePath); // pas 14b
        Assert.Null(loaded.Items[1].ArchivePath);
        Assert.Empty(RecentRecordingsFile.Read(Path.Combine(_folder, "no-hi-es.json")));
    }

    [Fact]
    public void Els_ajustos_dels_enregistradors_es_desen_a_la_configuracio()
    {
        var repository = new ConfigRepository(Path.Combine(_folder, "config.json"));
        var config = new EngineConfig();
        config.RecorderSettings["1"] = new RecorderSettings("Programa", @"D:\Enregistraments", 3.5);
        config.RecorderSettings["2"] = new RecorderSettings(null, null, 0);

        repository.Save(config);
        var loaded = repository.Load().Config;

        Assert.Equal(new RecorderSettings("Programa", @"D:\Enregistraments", 3.5), loaded.RecorderSettings["1"]);
        Assert.Equal(new RecorderSettings(null, null, 0), loaded.RecorderSettings["2"]);
    }

    [Fact]
    public void Avisa_quan_queda_poc_espai()
    {
        using var recorder = NewRecorder();
        Assert.False(recorder.LowDiskSpace); // el disc temporal té més de 288 MB lliures

        recorder.LowDiskHours = 1e9; // un llindar impossible
        Assert.True(recorder.LowDiskSpace);
        Assert.True(RecorderJson.Recorder(recorder)["lowDiskSpace"]!.GetValue<bool>());
    }
}
