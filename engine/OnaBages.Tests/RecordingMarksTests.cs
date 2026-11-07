using System.Globalization;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>
/// Marques durant l'enregistrament (fase 5, pas 23a; D-110): posició, fitxer <c>Nom.mp3.marques</c> al costat
/// de l'enregistrament, i que el segueixi en desar-lo, descartar-lo i a l'historial.
/// </summary>
public class RecordingMarksTests : IDisposable
{
    private const string DeviceId = "{0.0.1.00000000}.{proves}";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "marques-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _recycled = [];
    private FakeInput? _input;

    public RecordingMarksTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private Recorder NewRecorder(string id = "1", string? archive = null)
    {
        var recorder = new Recorder(id, _ => _input = new FakeInput());
        recorder.SetDevice(DeviceId);
        recorder.Configure(folder: _folder);
        recorder.SetArchive(archive);
        return recorder;
    }

    private (CommandRouter Router, EngineState State, RecordingList List) NewRouter()
    {
        var recorders = new[] { NewRecorder("1") };
        var list = new RecordingList(
            isPending: path => recorders.Any(r => r.State == RecorderState.Pending && r.FilePath == path),
            sendToRecycleBin: path => { _recycled.Add(path); File.Delete(path); }, // paperera falsa
            synchronous: true);
        foreach (var r in recorders) r.Finished += f => list.Add(f);
        var state = new EngineState([], recorders);
        return (new CommandRouter(state, () => { }, () => [], recordings: list), state, list);
    }

    private static string Escape(string path) => path.Replace("\\", "\\\\");

    [Fact]
    public void La_marca_es_a_la_posicio_de_l_enregistrament_i_es_desa_de_seguida()
    {
        using var recorder = NewRecorder();
        recorder.Start();
        _input!.PushTone(1.0);
        recorder.Mark();
        _input.PushTone(0.5);
        recorder.Mark();

        Assert.Equal([1.0, 1.5], recorder.Marks.Select(m => Math.Round(m.Seconds, 3)));
        // Ja és al disc abans d'aturar: si el motor caigués ara, les marques no es perdrien.
        string path = recorder.FilePath!;
        Assert.Equal([1.0, 1.5], RecordingMarks.Read(path).Select(m => Math.Round(m.Seconds, 3)));

        _input.PushTone(0.5);
        recorder.Stop();

        // Pendent: les marques es continuen veient.
        Assert.Equal(2, recorder.Marks.Count);
        Assert.Equal(2, RecordingMarks.Read(path).Count);
        Assert.False(File.Exists(RecordingMarks.PathFor(path) + ".tmp"));
    }

    [Fact]
    public void Nomes_es_poden_posar_marques_mentre_s_enregistra()
    {
        using var recorder = NewRecorder();
        Assert.Throws<RecorderCommandException>(() => recorder.Mark()); // inactiu

        recorder.Start();
        _input!.PushTone(0.5);
        recorder.Stop();
        Assert.Throws<RecorderCommandException>(() => recorder.Mark()); // pendent
        Assert.Empty(recorder.Marks);
        Assert.False(File.Exists(RecordingMarks.PathFor(recorder.FilePath!)), "Sense marques, no hi ha fitxer de marques");
    }

    [Fact]
    public void Un_enregistrament_nou_comenca_sense_marques()
    {
        using var recorder = NewRecorder();
        recorder.Start();
        _input!.PushTone(0.5);
        recorder.Mark();
        recorder.Stop();
        string first = recorder.FilePath!;

        recorder.Start();
        _input.PushTone(0.2);
        Assert.Empty(recorder.Marks);
        recorder.Mark();
        recorder.Stop();

        Assert.Single(RecordingMarks.Read(first));
        Assert.Equal(0.2, Math.Round(RecordingMarks.Read(recorder.FilePath!).Single().Seconds, 3));
    }

    [Fact]
    public void Ordre_recorder_mark_i_les_marques_a_l_estat()
    {
        var (router, state, _) = NewRouter();
        var recorder = state.FindRecorder("1")!;

        var bad = router.Handle("""{ "v": 1, "id": 1, "cmd": "recorder.mark", "recorder": "1" }""");
        Assert.False(bad["ok"]!.GetValue<bool>());

        recorder.Start();
        _input!.PushTone(2.0);
        var r = router.Handle("""{ "v": 1, "id": 2, "cmd": "recorder.mark", "recorder": "1" }""");

        Assert.True(r["ok"]!.GetValue<bool>());
        var marks = r["result"]!["recorder"]!["marks"]!.AsArray();
        Assert.Equal(2.0, marks.Single()!["seconds"]!.GetValue<double>(), 3);
        Assert.Single(RecorderJson.Recorder(recorder)["marks"]!.AsArray());
        recorder.Stop();
    }

    [Fact]
    public void El_nom_d_una_marca_es_desa_de_seguida_tambe_quan_es_pendent()
    {
        using var recorder = NewRecorder();
        recorder.Start();
        _input!.PushTone(0.5);
        recorder.Mark();
        recorder.Mark();

        recorder.NameMark(2, "  Entrevista  ");
        Assert.Equal([null, "Entrevista"], recorder.Marks.Select(m => m.Name));
        Assert.Equal("Entrevista", RecordingMarks.Read(recorder.FilePath!)[1].Name);

        recorder.Stop();
        recorder.NameMark(1, "Sintonia"); // pendent: encara es pot
        recorder.NameMark(2, "");         // buit: sense nom
        Assert.Equal(["Sintonia", null], RecordingMarks.Read(recorder.FilePath!).Select(m => m.Name));
    }

    [Fact]
    public void Un_nom_que_no_es_pot_posar()
    {
        using var recorder = NewRecorder();
        Assert.Throws<RecorderCommandException>(() => recorder.NameMark(1, "x")); // inactiu
        recorder.Start();
        _input!.PushTone(0.2);
        recorder.Mark();

        Assert.Throws<RecorderCommandException>(() => recorder.NameMark(2, "x")); // no hi és
        Assert.Throws<RecorderCommandException>(() => recorder.NameMark(0, "x"));
        Assert.Throws<RecorderCommandException>(() => recorder.NameMark(1, new string('a', Recorder.MaxMarkNameLength + 1)));
        Assert.Null(recorder.Marks.Single().Name);
        recorder.Stop();
    }

    [Fact]
    public void Ordre_recorder_nameMark()
    {
        var (router, state, _) = NewRouter();
        var recorder = state.FindRecorder("1")!;
        recorder.Start();
        _input!.PushTone(0.5);
        router.Handle("""{ "v": 1, "id": 1, "cmd": "recorder.mark", "recorder": "1" }""");

        var r = router.Handle("""{ "v": 1, "id": 2, "cmd": "recorder.nameMark", "recorder": "1", "number": 1, "name": "Notícies" }""");

        Assert.True(r["ok"]!.GetValue<bool>(), r.ToJsonString());
        Assert.Equal("Notícies", r["result"]!["recorder"]!["marks"]![0]!["name"]!.GetValue<string>());
        var bad = router.Handle("""{ "v": 1, "id": 3, "cmd": "recorder.nameMark", "recorder": "1", "number": 1, "name": 5 }""");
        Assert.Equal("bad_params", bad["error"]!["code"]!.GetValue<string>());
        recorder.Stop();
    }

    [Fact]
    public void En_desar_l_enregistrament_les_marques_el_segueixen()
    {
        var (router, state, list) = NewRouter();
        var recorder = state.FindRecorder("1")!;
        recorder.Start();
        _input!.PushTone(1.0);
        recorder.Mark();
        recorder.Stop();
        string source = recorder.FilePath!;
        string target = Path.Combine(_folder, "desats", "Entrevista.mp3");

        var r = router.Handle($$"""{ "v": 1, "id": 1, "cmd": "recording.save", "recordingId": "{{list.Items.Single().Id}}", "path": "{{Escape(target)}}" }""");

        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.True(File.Exists(target));
        Assert.False(File.Exists(RecordingMarks.PathFor(source)));
        Assert.Equal(1.0, Math.Round(RecordingMarks.Read(target).Single().Seconds, 3));
        Assert.Empty(recorder.Marks); // l'enregistrador torna a Inactiu, net
    }

    [Fact]
    public void En_descartar_l_enregistrament_les_marques_tambe_van_a_la_paperera()
    {
        var (router, state, list) = NewRouter();
        var recorder = state.FindRecorder("1")!;
        recorder.Start();
        _input!.PushTone(0.5);
        recorder.Mark();
        recorder.Stop();
        string path = recorder.FilePath!;

        router.Handle($$"""{ "v": 1, "id": 1, "cmd": "recording.discard", "recordingId": "{{list.Items.Single().Id}}" }""");

        Assert.Equal([path, RecordingMarks.PathFor(path)], _recycled);
        Assert.False(File.Exists(RecordingMarks.PathFor(path)));
    }

    [Fact]
    public void L_historial_tambe_te_les_marques_i_la_neteja_les_esborra()
    {
        string archive = Path.Combine(_folder, "historial");
        using var recorder = NewRecorder(archive: archive);
        recorder.Start();
        _input!.PushTone(0.5);
        recorder.Mark();
        recorder.Stop();
        Assert.Single(RecordingMarks.Read(recorder.ArchivePath!));

        // Un dia antic de l'historial: l'MP3 i les seves marques s'esborren.
        string old = Path.Combine(archive, DateTime.Today.AddDays(-40).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(old);
        File.WriteAllBytes(Path.Combine(old, "Vell.mp3"), [1, 2, 3]);
        RecordingMarks.Write(Path.Combine(old, "Vell.mp3"), [new RecordingMark(1)]);

        RecordingArchive.DeleteOld(archive, 30, DateTime.Today);

        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public void Unes_marques_malmeses_no_fallen_i_es_llegeixen_ordenades()
    {
        string mp3 = Path.Combine(_folder, "a.mp3");
        File.WriteAllText(RecordingMarks.PathFor(mp3), "{ això no és JSON");
        Assert.Empty(RecordingMarks.Read(mp3));

        File.WriteAllText(RecordingMarks.PathFor(mp3),
            """{ "schemaVersion": 1, "marks": [ { "seconds": 9.5 }, { "seconds": "x" }, { "seconds": -1 }, { "seconds": 2, "name": "Entrevista" } ] }""");
        var marks = RecordingMarks.Read(mp3);

        Assert.Equal([2.0, 9.5], marks.Select(m => m.Seconds));
        Assert.Equal("Entrevista", marks[0].Name);
    }
}
