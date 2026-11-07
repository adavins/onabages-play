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
        // S'espera que acabi la forma d'ona: mentre es calcula, el fitxer és obert i no es pot desar a sobre.
        for (int i = 0; i < 200 && editor["peaksDone"]!.GetValue<int>() < editor["originalFrames"]!.GetValue<int>(); i++)
        {
            Thread.Sleep(25);
            editor = Send("editor.get");
        }
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
    public void Moure_una_marca_es_desa_de_seguida_i_no_modifica_l_audio()
    {
        string mp3 = CreateMp3(3);
        RecordingMarks.Write(mp3, [new RecordingMark(0.6), new RecordingMark(1.5, "B")]);
        Open(mp3);

        var editor = Send("editor.moveMark", new JsonObject { ["number"] = 2, ["seconds"] = 0.9 });

        Assert.Equal([(1, 0.6), (2, 0.9)], Marks(editor));
        Assert.False(editor["modified"]!.GetValue<bool>()); // l'àudio no canvia: no cal desar
        Assert.False(editor["canUndo"]!.GetValue<bool>());
        var saved = RecordingMarks.Read(mp3);
        Assert.Equal([0.6, 0.9], saved.Select(m => Math.Round(m.Seconds, 3)));
        Assert.Equal("B", saved[1].Name); // el nom es manté
    }

    [Fact]
    public void Moure_una_marca_despres_d_un_tall_la_desa_en_temps_de_l_original()
    {
        string mp3 = CreateMp3(3);
        RecordingMarks.Write(mp3, [new RecordingMark(0.6), new RecordingMark(2.4)]);
        Open(mp3);
        Send("editor.delete", new JsonObject { ["start"] = 0, ["end"] = 20 * Frame }); // −0,48 s

        // Passar la 1 per darrere de la 2: es renumeren per ordre de temps (D-125; abans conservava el número).
        var editor = Send("editor.moveMark", new JsonObject { ["number"] = 1, ["seconds"] = 2.0 });

        Assert.Equal([(1, 1.92), (2, 2.0)], Marks(editor));
        Assert.Equal([2.4, 2.48], RecordingMarks.Read(mp3).Select(m => Math.Round(m.Seconds, 3)));
        Assert.True(editor["modified"]!.GetValue<bool>()); // pel tall, no per la marca
    }

    [Fact]
    public void No_es_pot_moure_una_marca_que_no_es_veu()
    {
        string mp3 = CreateMp3(3);
        RecordingMarks.Write(mp3, [new RecordingMark(0.24)]);
        Open(mp3);
        Send("editor.delete", new JsonObject { ["start"] = 0, ["end"] = 20 * Frame }); // la marca 1 hi era

        var msg = new JsonObject { ["v"] = 1, ["id"] = 99, ["cmd"] = "editor.moveMark", ["number"] = 1, ["seconds"] = 1.0 };
        Assert.Equal("bad_state", _router.Handle(msg.ToJsonString())["error"]!["code"]!.GetValue<string>());
        msg["number"] = 2;
        Assert.Equal("bad_state", _router.Handle(msg.ToJsonString())["error"]!["code"]!.GetValue<string>());
        Assert.Equal(0.24, RecordingMarks.Read(mp3).Single().Seconds);
    }

    // --- Afegir, esborrar i posar nom des de l'editor (fase 5, pas 27a; D-125) ---

    private JsonObject Fail(string cmd, JsonObject fields)
    {
        var msg = new JsonObject { ["v"] = 1, ["id"] = ++_id, ["cmd"] = cmd };
        foreach (var (key, value) in fields) msg[key] = value?.DeepClone();
        var r = _router.Handle(msg.ToJsonString());
        Assert.False(r["ok"]!.GetValue<bool>(), r.ToJsonString());
        return r["error"]!.AsObject();
    }

    [Fact]
    public void Afegir_una_marca_al_mig_renumera_les_seguents_i_es_desa_de_seguida()
    {
        string mp3 = CreateMp3(3);
        RecordingMarks.Write(mp3, [new RecordingMark(0.6, "A"), new RecordingMark(2.0, "B")]);
        Open(mp3);

        var r = _router.Handle(new JsonObject { ["v"] = 1, ["id"] = 50, ["cmd"] = "editor.addMark", ["seconds"] = 1.2, ["name"] = "  Nova " }.ToJsonString());
        Assert.Equal(2, r["result"]!["number"]!.GetValue<int>());
        var editor = r["result"]!["editor"]!.AsObject();

        Assert.Equal([(1, 0.6), (2, 1.2), (3, 2.0)], Marks(editor));
        Assert.False(editor["modified"]!.GetValue<bool>());
        Assert.Equal(["A", "Nova", "B"], RecordingMarks.Read(mp3).Select(m => m.Name));
    }

    [Fact]
    public void Un_fitxer_sense_marques_en_pot_tenir_i_despres_d_un_tall_va_en_temps_de_l_original()
    {
        string mp3 = CreateMp3(3);
        Open(mp3);
        Send("editor.delete", new JsonObject { ["start"] = 0, ["end"] = 20 * Frame }); // −0,48 s

        var editor = Send("editor.addMark", new JsonObject { ["seconds"] = 1.0 });

        Assert.Equal([(1, 1.0)], Marks(editor));
        var saved = RecordingMarks.Read(mp3).Single();
        Assert.Equal(1.48, Math.Round(saved.Seconds, 3));
        Assert.Null(saved.Name);
    }

    [Fact]
    public void Esborrar_i_posar_nom()
    {
        string mp3 = CreateMp3(3);
        RecordingMarks.Write(mp3, [new RecordingMark(0.6), new RecordingMark(1.5), new RecordingMark(2.0)]);
        Open(mp3);

        var editor = Send("editor.removeMark", new JsonObject { ["number"] = 2 });
        Assert.Equal([(1, 0.6), (2, 2.0)], Marks(editor));

        editor = Send("editor.nameMark", new JsonObject { ["number"] = 2, ["name"] = "Entrevista" });
        Assert.Equal("Entrevista", editor["marks"]![1]!["name"]!.GetValue<string>());
        Assert.Equal([null, "Entrevista"], RecordingMarks.Read(mp3).Select(m => m.Name));

        Send("editor.nameMark", new JsonObject { ["number"] = 2, ["name"] = "" });   // treure el nom
        Assert.Null(RecordingMarks.Read(mp3)[1].Name);

        Send("editor.removeMark", new JsonObject { ["number"] = 1 });
        Send("editor.removeMark", new JsonObject { ["number"] = 1 });
        Assert.Empty(RecordingMarks.Read(mp3));
    }

    [Fact]
    public void Errors_de_les_marques_de_l_editor()
    {
        string mp3 = CreateMp3(3);
        RecordingMarks.Write(mp3, [new RecordingMark(0.24)]);
        Open(mp3);
        string tooLong = new('x', 61);

        Assert.Equal("bad_params", Fail("editor.nameMark", new JsonObject { ["number"] = 1, ["name"] = tooLong })["code"]!.GetValue<string>());
        Assert.Equal("bad_params", Fail("editor.nameMark", new JsonObject { ["number"] = 1 })["code"]!.GetValue<string>());
        Assert.Equal("bad_params", Fail("editor.addMark", new JsonObject { ["seconds"] = 1, ["name"] = tooLong })["code"]!.GetValue<string>());
        Assert.Equal("bad_state", Fail("editor.removeMark", new JsonObject { ["number"] = 3 })["code"]!.GetValue<string>());
        // Una marca d'un tros eliminat no es pot esborrar ni canviar (no es veu).
        Send("editor.delete", new JsonObject { ["start"] = 0, ["end"] = 20 * Frame });
        Assert.Equal("bad_state", Fail("editor.removeMark", new JsonObject { ["number"] = 1 })["code"]!.GetValue<string>());
        Assert.Equal("bad_state", Fail("editor.nameMark", new JsonObject { ["number"] = 1, ["name"] = "x" })["code"]!.GetValue<string>());
        Assert.Single(RecordingMarks.Read(mp3));
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
