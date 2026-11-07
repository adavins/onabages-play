using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>
/// Marques d'un enregistrament a l'editor (fase 5, pas 23c; D-112): es veuen on queden a la versió editada, les
/// dels trossos eliminats no hi surten, i en desar es desen ajustades al costat del fitxer desat.
/// </summary>
public class EditorMarksTests : IDisposable
{
    private const int Rate = 48000;
    private const double Frame = 1152.0 / Rate; // 0,024 s
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "editor-marques-" + Guid.NewGuid().ToString("N"));
    private readonly EngineState _state;
    private readonly CommandRouter _router;
    private int _id;

    public EditorMarksTests()
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
        var r = _router.Handle(msg.ToJsonString());
        Assert.True(r["ok"]!.GetValue<bool>(), r.ToJsonString());
        return r["result"]!["editor"]!.AsObject();
    }

    private JsonObject WaitIdle()
    {
        for (int i = 0; i < 200; i++)
        {
            var editor = Send("editor.get");
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

    private JsonObject Open(string path)
    {
        Send("editor.open", new JsonObject { ["path"] = path });
        var editor = WaitIdle();
        Assert.Null(editor["error"]);
        return editor;
    }

    private static (int Number, double Seconds)[] Marks(JsonObject editor) =>
        editor["marks"]!.AsArray().Select(m => (m!["number"]!.GetValue<int>(), m["seconds"]!.GetValue<double>())).ToArray();

    [Fact]
    public void En_obrir_es_veuen_les_marques_del_fitxer()
    {
        string mp3 = CreateMp3(3);
        RecordingMarks.Write(mp3, [new RecordingMark(0.6), new RecordingMark(2.0, "Entrevista")]);

        var editor = Open(mp3);

        Assert.Equal([(1, 0.6), (2, 2.0)], Marks(editor));
        Assert.Equal("Entrevista", editor["marks"]![1]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Un_fitxer_sense_marques_no_en_te()
    {
        Assert.Empty(Open(CreateMp3(1))["marks"]!.AsArray());
    }

    [Fact]
    public void Les_marques_segueixen_els_talls_i_el_desfer()
    {
        string mp3 = CreateMp3(3);
        RecordingMarks.Write(mp3, [new RecordingMark(0.6), new RecordingMark(1.5), new RecordingMark(2.4)]);
        Open(mp3);

        // S'elimina el principi (20 fotogrames): totes s'avancen 0,48 s.
        var editor = Send("editor.delete", new JsonObject { ["start"] = 0, ["end"] = 20 * Frame });
        Assert.Equal([(1, 0.12), (2, 1.02), (3, 1.92)], Marks(editor));

        // S'elimina un tros que conté la 2 (1,02 s a la versió editada): ja no hi surt, i la 3 conserva el número.
        editor = Send("editor.delete", new JsonObject { ["start"] = 40 * Frame, ["end"] = 50 * Frame });
        Assert.Equal([(1, 0.12), (3, 1.68)], Marks(editor));

        editor = Send("editor.undo");
        Assert.Equal([(1, 0.12), (2, 1.02), (3, 1.92)], Marks(editor));
    }

    [Fact]
    public void Una_marca_del_final_eliminat_no_queda_enganxada_al_final_que_es_conserva()
    {
        var marks = RecordingMarks.MapToEdited([new RecordingMark(80 * Frame), new RecordingMark(30 * Frame)],
            [new FrameRange(0, 50)], Frame, originalFrames: 100);

        var kept = Assert.Single(marks);
        Assert.Equal(2, kept.Number);
        Assert.Equal(30 * Frame, kept.Mark.Seconds, 6);
    }

    [Fact]
    public void En_desar_com_a_fitxer_nou_les_marques_s_hi_desen_ajustades()
    {
        string mp3 = CreateMp3(3);
        RecordingMarks.Write(mp3, [new RecordingMark(0.6), new RecordingMark(2.4)]);
        Open(mp3);
        Send("editor.delete", new JsonObject { ["start"] = 0, ["end"] = 20 * Frame });
        string target = Path.Combine(_folder, "editat.mp3");

        Send("editor.save", new JsonObject { ["path"] = target });
        var editor = WaitIdle();

        Assert.Null(editor["error"]);
        Assert.Equal([0.12, 1.92], RecordingMarks.Read(target).Select(m => Math.Round(m.Seconds, 3)));
        Assert.Equal([0.6, 2.4], RecordingMarks.Read(mp3).Select(m => m.Seconds)); // l'original, intacte
        // L'editor continua amb el fitxer desat: les marques ja són les seves, numerades de nou.
        Assert.Equal([(1, 0.12), (2, 1.92)], Marks(editor));
    }

    [Fact]
    public void En_desar_a_sobre_sense_cap_marca_el_fitxer_de_marques_desapareix()
    {
        string mp3 = CreateMp3(3);
        RecordingMarks.Write(mp3, [new RecordingMark(0.24)]);
        Open(mp3);
        Send("editor.delete", new JsonObject { ["start"] = 0, ["end"] = 20 * Frame });

        Send("editor.save", new JsonObject { ["overwrite"] = true });
        var editor = WaitIdle();

        Assert.Null(editor["error"]);
        Assert.False(File.Exists(RecordingMarks.PathFor(mp3)));
        Assert.Empty(editor["marks"]!.AsArray());
    }
}
