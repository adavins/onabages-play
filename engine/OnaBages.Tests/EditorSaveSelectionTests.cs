using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>
/// Desar només la selecció (fase 5, pas 23d; D-113): un fitxer nou amb el tros seleccionat, sense canviar
/// l'edició, amb les marques de dins.
/// </summary>
public class EditorSaveSelectionTests : IDisposable
{
    private const int Rate = 48000;
    private const double Frame = 1152.0 / Rate; // 0,024 s
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "editor-seleccio-" + Guid.NewGuid().ToString("N"));
    private readonly EngineState _state;
    private readonly CommandRouter _router;
    private int _id;

    public EditorSaveSelectionTests()
    {
        Directory.CreateDirectory(_folder);
        _state = new EngineState([], [], editor: new EditorSession(Path.Combine(_folder, "cache")));
        _router = new CommandRouter(_state, () => { }, () => []);
    }

    public void Dispose()
    {
        _state.Editor.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private JsonObject Send(string cmd, JsonObject? fields = null)
    {
        var msg = new JsonObject { ["v"] = 1, ["id"] = ++_id, ["cmd"] = cmd };
        foreach (var (key, value) in fields ?? [])
            msg[key] = value?.DeepClone();
        return _router.Handle(msg.ToJsonString());
    }

    private static JsonObject Editor(JsonObject r)
    {
        Assert.True(r["ok"]!.GetValue<bool>(), r.ToJsonString());
        return r["result"]!["editor"]!.AsObject();
    }

    private static string ErrorCode(JsonObject r) => r["error"]!["code"]!.GetValue<string>();

    private JsonObject WaitIdle()
    {
        for (int i = 0; i < 200; i++)
        {
            var editor = Editor(Send("editor.get"));
            if (editor["busy"] is null) return editor;
            Thread.Sleep(25);
        }
        throw new TimeoutException("L'editor no ha acabat a temps.");
    }

    private string CreateMp3(double seconds, string name = "prova.mp3")
    {
        string wav = TestAudio.CreateWav(Rate, 2, seconds);
        string path = Path.Combine(_folder, name);
        using (var source = AudioFileSource.Open(wav, Rate))
        using (var encoder = new LameEncoder(Rate, 320))
        using (var file = File.Create(path))
        {
            var buffer = new float[Rate];
            int read;
            while ((read = source.Read(buffer)) > 0) file.Write(encoder.Encode(buffer, read));
            file.Write(encoder.Flush());
        }
        File.Delete(wav);
        return path;
    }

    private void Open(string path)
    {
        Editor(Send("editor.open", new JsonObject { ["path"] = path }));
        Assert.Null(WaitIdle()["error"]);
    }

    private JsonObject SaveSelection(double startFrames, double endFrames, string target, bool overwrite = false) =>
        Send("editor.saveSelection", new JsonObject
        {
            ["start"] = startFrames * Frame, ["end"] = endFrames * Frame, ["path"] = target, ["overwrite"] = overwrite,
        });

    [Fact]
    public void Desa_nomes_la_seleccio_i_l_edicio_no_canvia()
    {
        string mp3 = CreateMp3(3);
        Open(mp3);
        Editor(Send("editor.delete", new JsonObject { ["start"] = 0, ["end"] = 10 * Frame })); // un canvi per desfer
        var before = Editor(Send("editor.get"));
        string target = Path.Combine(_folder, "tros.mp3");

        var saving = Editor(SaveSelection(20, 60, target));
        Assert.Equal("saving", saving["busy"]!.GetValue<string>());
        var editor = WaitIdle();

        Assert.Null(editor["error"]);
        Assert.Equal(target, editor["savedSelectionPath"]!.GetValue<string>());
        Assert.Equal(40, Mp3FrameIndex.Build(target).FrameCount);
        // L'editor continua igual: el mateix fitxer, els mateixos canvis, i es pot desfer.
        Assert.Equal(mp3, editor["path"]!.GetValue<string>());
        Assert.Null(editor["savedPath"]);
        Assert.Equal(before["segments"]!.ToJsonString(), editor["segments"]!.ToJsonString());
        Assert.True(editor["modified"]!.GetValue<bool>());
        Assert.True(editor["canUndo"]!.GetValue<bool>());

        // La propera ordre ja no el porta.
        Assert.Null(Editor(Send("editor.undo"))["savedSelectionPath"]);
    }

    [Fact]
    public void Els_fotogrames_son_els_de_la_seleccio()
    {
        string mp3 = CreateMp3(2);
        Open(mp3);
        Editor(Send("editor.delete", new JsonObject { ["start"] = 30 * Frame, ["end"] = 40 * Frame })); // un tall al mig
        string target = Path.Combine(_folder, "tros.mp3");

        Editor(SaveSelection(25, 45, target)); // travessa el tall: 25..29 i 40..54 de l'original
        WaitIdle();

        var original = Mp3FrameIndex.Build(mp3);
        var saved = Mp3FrameIndex.Build(target);
        Assert.Equal(20, saved.FrameCount);
        byte[] src = File.ReadAllBytes(mp3), dst = File.ReadAllBytes(target);
        int[] expected = [.. Enumerable.Range(25, 5), .. Enumerable.Range(40, 15)];
        for (int i = 0; i < expected.Length; i++)
        {
            var a = src.AsSpan((int)original.Offset(expected[i]), original.Length(expected[i]));
            var b = dst.AsSpan((int)saved.Offset(i), saved.Length(i));
            Assert.True(a.SequenceEqual(b), $"El fotograma {i} hauria de ser el {expected[i]} de l'original");
        }
    }

    [Fact]
    public void Les_marques_de_dins_es_desen_comptades_des_del_principi()
    {
        string mp3 = CreateMp3(3);
        RecordingMarks.Write(mp3, [new RecordingMark(10 * Frame), new RecordingMark(30 * Frame), new RecordingMark(50 * Frame, "B"), new RecordingMark(90 * Frame)]);
        Open(mp3);
        string target = Path.Combine(_folder, "tros.mp3");

        Editor(SaveSelection(20, 60, target));
        WaitIdle();

        var marks = RecordingMarks.Read(target);
        Assert.Equal([0.24, 0.72], marks.Select(m => Math.Round(m.Seconds, 3))); // 10 i 30 fotogrames des del principi
        Assert.Equal("B", marks[1].Name);
        Assert.Equal(4, RecordingMarks.Read(mp3).Count); // les de l'original, intactes
    }

    [Fact]
    public void Sense_marques_dins_no_hi_ha_fitxer_de_marques()
    {
        string mp3 = CreateMp3(3);
        RecordingMarks.Write(mp3, [new RecordingMark(100 * Frame)]);
        Open(mp3);
        string target = Path.Combine(_folder, "tros.mp3");

        Editor(SaveSelection(0, 40, target));
        WaitIdle();

        Assert.False(File.Exists(RecordingMarks.PathFor(target)));
    }

    [Fact]
    public void Sobreescriure_demana_confirmacio_i_no_es_pot_desar_a_sobre_del_fitxer_obert()
    {
        string mp3 = CreateMp3(2);
        Open(mp3);
        string target = Path.Combine(_folder, "ja-hi-es.mp3");
        File.WriteAllText(target, "no és un mp3");

        Assert.Equal("file_exists", ErrorCode(SaveSelection(0, 20, target)));
        Assert.Equal("no és un mp3", File.ReadAllText(target));

        Editor(SaveSelection(0, 20, target, overwrite: true));
        Assert.Null(WaitIdle()["error"]);
        Assert.Equal(20, Mp3FrameIndex.Build(target).FrameCount);

        Assert.Equal("bad_state", ErrorCode(SaveSelection(0, 20, mp3, overwrite: true)));
        Assert.Equal("bad_state", ErrorCode(SaveSelection(20, 20, Path.Combine(_folder, "buit.mp3")))); // sense selecció
    }
}
