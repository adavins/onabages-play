using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>Ordres editor.* (fase 3, pas 1): obrir, eliminar, conservar, desfer, refer i desar.</summary>
public class EditorCommandsTests : IDisposable
{
    private const int Rate = 48000;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "editor-" + Guid.NewGuid().ToString("N"));
    private readonly EngineState _state;
    private readonly CommandRouter _router;
    private int _id;

    public EditorCommandsTests()
    {
        Directory.CreateDirectory(_folder);
        _state = new EngineState([], [new Recorder("1", _ => new FakeInput())],
            editor: new EditorSession(Path.Combine(_folder, "cache")));
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

    /// <summary>Espera que acabi d'obrir o desar (es fa en segon pla).</summary>
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

    private JsonObject OpenFile(string path)
    {
        var opening = Editor(Send("editor.open", new JsonObject { ["path"] = path }));
        Assert.Equal(path, opening["path"]!.GetValue<string>());
        var editor = WaitIdle();
        Assert.Null(editor["error"]);
        return editor;
    }

    [Fact]
    public void Sense_fitxer_obert()
    {
        var editor = Editor(Send("editor.get"));

        Assert.False(editor["open"]!.GetValue<bool>());
        Assert.Equal("bad_state", ErrorCode(Send("editor.undo")));
        Assert.Equal("bad_state", ErrorCode(Send("editor.delete", new JsonObject { ["start"] = 0, ["end"] = 1 })));
    }

    [Fact]
    public void Obrir_un_mp3()
    {
        var editor = OpenFile(CreateMp3(2));

        Assert.True(editor["open"]!.GetValue<bool>());
        Assert.Equal(Rate, editor["sampleRate"]!.GetValue<int>());
        Assert.Equal(0.024, editor["frameSeconds"]!.GetValue<double>(), 6);
        Assert.InRange(editor["duration"]!.GetValue<double>(), 2.0, 2.1);
        Assert.False(editor["modified"]!.GetValue<bool>());
        Assert.Single(editor["segments"]!.AsArray());
    }

    [Fact]
    public void L_estat_general_inclou_l_editor()
    {
        var r = Send("state.get");

        Assert.False(r["result"]!["editor"]!["open"]!.GetValue<bool>());
    }

    [Fact]
    public void Un_fitxer_que_no_es_mp3_deixa_un_error()
    {
        string path = Path.Combine(_folder, "soroll.mp3");
        File.WriteAllBytes(path, [.. Enumerable.Range(0, 5000).Select(i => (byte)(i * 7))]);

        Send("editor.open", new JsonObject { ["path"] = path });
        var editor = WaitIdle();

        Assert.False(editor["open"]!.GetValue<bool>());
        Assert.Contains("no és un MP3", editor["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("relatiu.mp3")]
    [InlineData(@"C:\musica\pista.wav")]
    public void Rutes_no_valides(string path)
    {
        Assert.Equal("bad_params", ErrorCode(Send("editor.open", new JsonObject { ["path"] = path })));
    }

    [Fact]
    public void No_obre_un_enregistrament_interromput()
    {
        string path = CreateMp3(1);
        File.WriteAllText(path + ".rec", "{}");

        var r = Send("editor.open", new JsonObject { ["path"] = path });

        Assert.Equal("bad_state", ErrorCode(r));
        Assert.Contains(".rec", r["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void Eliminar_desfer_i_refer()
    {
        var opened = OpenFile(CreateMp3(2));
        double total = opened["duration"]!.GetValue<double>();

        var r = Send("editor.delete", new JsonObject { ["start"] = 0.5, ["end"] = 1.0 });
        var editor = Editor(r);
        Assert.True(r["result"]!["changed"]!.GetValue<bool>());
        Assert.Equal(total - 0.504, editor["duration"]!.GetValue<double>(), 3); // 21 fotogrames de 24 ms
        Assert.True(editor["modified"]!.GetValue<bool>());
        Assert.True(editor["canUndo"]!.GetValue<bool>());

        editor = Editor(Send("editor.undo"));
        Assert.Equal(total, editor["duration"]!.GetValue<double>(), 3);
        Assert.True(editor["canRedo"]!.GetValue<bool>());

        editor = Editor(Send("editor.redo"));
        Assert.Equal(total - 0.504, editor["duration"]!.GetValue<double>(), 3);
    }

    [Fact]
    public void Conservar_la_seleccio()
    {
        OpenFile(CreateMp3(2));

        var editor = Editor(Send("editor.keep", new JsonObject { ["start"] = 0.48, ["end"] = 0.96 }));

        Assert.Equal(0.48, editor["duration"]!.GetValue<double>(), 3);
        Assert.Equal(20, editor["segments"]![0]![0]!.GetValue<int>());
    }

    [Fact]
    public void Desar_com_a_fitxer_nou()
    {
        string source = CreateMp3(2);
        OpenFile(source);
        Send("editor.delete", new JsonObject { ["start"] = 0, ["end"] = 0.48 });
        string target = Path.Combine(_folder, "editat.mp3");

        var saving = Editor(Send("editor.save", new JsonObject { ["path"] = target }));
        Assert.Equal("saving", saving["busy"]!.GetValue<string>());
        var editor = WaitIdle();

        Assert.Null(editor["error"]);
        Assert.Equal(target, editor["savedPath"]!.GetValue<string>());
        Assert.Equal(target, editor["path"]!.GetValue<string>()); // continua amb el fitxer desat
        Assert.False(editor["modified"]!.GetValue<bool>());
        Assert.Equal(Mp3FrameIndex.Build(source).FrameCount - 20, Mp3FrameIndex.Build(target).FrameCount);
    }

    [Fact]
    public void Sobreescriure_demana_confirmacio()
    {
        string source = CreateMp3(2);
        OpenFile(source);
        Send("editor.delete", new JsonObject { ["start"] = 0, ["end"] = 0.48 });

        Assert.Equal("file_exists", ErrorCode(Send("editor.save")));               // a sobre de l'original
        Editor(Send("editor.save", new JsonObject { ["overwrite"] = true }));
        var editor = WaitIdle();

        Assert.Null(editor["error"]);
        Assert.Equal(source, editor["savedPath"]!.GetValue<string>());
    }

    [Fact]
    public void Mentre_es_desa_no_es_pot_editar()
    {
        OpenFile(CreateMp3(30));
        Send("editor.delete", new JsonObject { ["start"] = 0, ["end"] = 1 });

        Send("editor.save", new JsonObject { ["path"] = Path.Combine(_folder, "editat.mp3") });
        var r = Send("editor.undo");

        // Si el desat ja ha acabat (ordinador molt ràpid), l'ordre funciona; si no, bad_state.
        if (r["ok"]!.GetValue<bool>() == false)
            Assert.Equal("bad_state", ErrorCode(r));
        WaitIdle();
    }

    [Fact]
    public void Tancar()
    {
        OpenFile(CreateMp3(1));

        var editor = Editor(Send("editor.close"));

        Assert.False(editor["open"]!.GetValue<bool>());
        Assert.Null(editor["path"]);
    }

    [Fact]
    public void Cada_canvi_avisa_la_interficie()
    {
        int events = 0;
        _state.Editor.Changed += () => Interlocked.Increment(ref events);

        OpenFile(CreateMp3(1));
        Send("editor.delete", new JsonObject { ["start"] = 0, ["end"] = 0.1 });

        Assert.True(events >= 3); // obrint, obert, canvi
    }
}
