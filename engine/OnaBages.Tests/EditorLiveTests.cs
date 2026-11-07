using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>
/// Editar un enregistrament en curs (fase 5, pas 24a; D-117): l'editor n'obre una fotografia que només es llegeix;
/// es poden desar trossos, i l'enregistrament continua intacte.
/// </summary>
public class EditorLiveTests : IDisposable
{
    private const string DeviceId = "{0.0.1.00000000}.{proves}";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "editor-viu-" + Guid.NewGuid().ToString("N"));
    private readonly Recorder _recorder;
    private readonly EngineState _state;
    private readonly CommandRouter _router;
    private FakeInput? _input;
    private int _id;

    public EditorLiveTests()
    {
        Directory.CreateDirectory(_folder);
        _recorder = new Recorder("1", _ => _input = new FakeInput());
        _recorder.SetDevice(DeviceId);
        _recorder.Configure(folder: _folder);
        _state = new EngineState([], [_recorder], editor: new EditorSession(Path.Combine(_folder, "cache")));
        _router = new CommandRouter(_state, () => { }, () => []);
    }

    public void Dispose()
    {
        _state.Editor.Dispose();
        _recorder.Dispose();
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
        for (int i = 0; i < 400; i++)
        {
            var editor = Editor(Send("editor.get"));
            if (editor["busy"] is null) return editor;
            Thread.Sleep(25);
        }
        throw new TimeoutException("L'editor no ha acabat a temps.");
    }

    /// <summary>
    /// Enregistra <paramref name="seconds"/> més i espera que el fitxer en tingui almenys <paramref name="seconds"/>
    /// en total al disc (el fil de codificació va una mica endarrere i bolca cada 2 s).
    /// </summary>
    private void RecordAndWait(double seconds, double totalOnDisk)
    {
        _input!.PushTone(seconds);
        long bytes = (long)(totalOnDisk * 320_000 / 8);
        for (int i = 0; i < 200 && new FileInfo(_recorder.FilePath!).Length < bytes; i++)
            Thread.Sleep(25);
        Assert.True(new FileInfo(_recorder.FilePath!).Length >= bytes, "El fitxer no ha crescut prou a temps");
    }

    [Fact]
    public void S_obre_mentre_s_enregistra_i_nomes_es_pot_llegir()
    {
        _recorder.Start();
        RecordAndWait(1.0, 0);
        _recorder.Mark();
        RecordAndWait(3.0, 3.5);
        string path = _recorder.FilePath!;

        Editor(Send("editor.open", new JsonObject { ["path"] = path }));
        var editor = WaitIdle();

        Assert.Null(editor["error"]);
        Assert.True(editor["live"]!.GetValue<bool>());
        Assert.InRange(editor["duration"]!.GetValue<double>(), 3.4, 4.1);
        Assert.Equal(1.0, editor["marks"]![0]!["seconds"]!.GetValue<double>(), 1);

        // No es pot editar ni desar a sobre (les marques sí que es poden moure: vegeu les proves del pas 24b).
        Assert.Equal("bad_state", ErrorCode(Send("editor.delete", new JsonObject { ["start"] = 0, ["end"] = 1 })));
        Assert.Equal("bad_state", ErrorCode(Send("editor.fadeIn", new JsonObject { ["start"] = 0, ["end"] = 1 })));
        Assert.Equal("bad_state", ErrorCode(Send("editor.save", new JsonObject { ["overwrite"] = true })));
        Assert.Equal("bad_state", ErrorCode(Send("editor.save", new JsonObject { ["path"] = Path.Combine(_folder, "x.mp3") })));
        _recorder.Stop();
    }

    [Fact]
    public void Es_pot_desar_un_tros_i_l_enregistrament_queda_intacte()
    {
        _recorder.Start();
        RecordAndWait(1.0, 0);
        _recorder.Mark();
        RecordAndWait(3.0, 3.5);
        string path = _recorder.FilePath!;
        Editor(Send("editor.open", new JsonObject { ["path"] = path }));
        WaitIdle();

        // Mentre es desa el tros, l'enregistrament continua.
        string piece = Path.Combine(_folder, "tros.mp3");
        Editor(Send("editor.saveSelection", new JsonObject { ["start"] = 0.5, ["end"] = 2.5, ["path"] = piece }));
        _input!.PushTone(2.0);
        var editor = WaitIdle();

        Assert.Null(editor["error"]);
        Assert.Equal(piece, editor["savedSelectionPath"]!.GetValue<string>());
        Assert.InRange(Mp3FrameIndex.Build(piece).Duration.TotalSeconds, 1.95, 2.05);
        Assert.Equal(0.5, RecordingMarks.Read(piece).Single().Seconds, 1); // la marca d'1 s, des del principi del tros

        // L'enregistrament: 6 s seguits, sense perdre res ni fotogrames tallats.
        _recorder.Stop();
        Assert.Equal(RecorderState.Pending, _recorder.State);
        Assert.Equal(0, _recorder.SamplesLost);
        Assert.Equal(6.0, _recorder.Elapsed.TotalSeconds, 2);
        var analysis = Mp3Frames.Analyze(path);
        Assert.Equal(0, analysis.TrailingBytes);
        Assert.InRange(analysis.Frames * 1152 / 48000.0, 6.0, 6.2);
        Assert.False(File.Exists(path + ".rec"));
    }

    [Fact]
    public void Actualitzar_hi_afegeix_el_que_s_ha_enregistrat_i_en_aturar_passa_a_normal()
    {
        _recorder.Start();
        RecordAndWait(3.0, 2.5);
        string path = _recorder.FilePath!;
        Editor(Send("editor.open", new JsonObject { ["path"] = path }));
        double before = WaitIdle()["duration"]!.GetValue<double>();

        RecordAndWait(3.0, before + 2.5);
        _recorder.Mark();
        RecordAndWait(0.5, 6.3); // 6,5 s enregistrats i la marca a 6 s: que quedi dins de la fotografia
        Editor(Send("editor.refresh"));
        var editor = WaitIdle();

        Assert.True(editor["live"]!.GetValue<bool>());
        Assert.True(editor["duration"]!.GetValue<double>() > before + 2.4, $"{editor["duration"]} > {before} + 2,4");
        Assert.Single(editor["marks"]!.AsArray());

        _recorder.Stop();
        Editor(Send("editor.refresh"));
        editor = WaitIdle();

        Assert.False(editor["live"]!.GetValue<bool>());
        Assert.Equal(6.5, editor["duration"]!.GetValue<double>(), 1);
        // Ja és un fitxer normal: es pot editar.
        Assert.True(Send("editor.delete", new JsonObject { ["start"] = 0, ["end"] = 1 })["ok"]!.GetValue<bool>());
    }

    // --- Pas 24b (D-118): les marques es mouen a través de l'enregistrador ---

    private static double[] Seconds(JsonObject editor) =>
        editor["marks"]!.AsArray().Select(m => Math.Round(m!["seconds"]!.GetValue<double>(), 2)).ToArray();

    [Fact]
    public void Moure_una_marca_mentre_s_enregistra_no_es_perd_amb_la_marca_seguent()
    {
        _recorder.Start();
        RecordAndWait(1.0, 0);
        _recorder.Mark();
        RecordAndWait(2.0, 2.5);
        Editor(Send("editor.open", new JsonObject { ["path"] = _recorder.FilePath! }));
        WaitIdle();

        var editor = Editor(Send("editor.moveMark", new JsonObject { ["number"] = 1, ["seconds"] = 2.0 }));

        Assert.Equal([2.0], Seconds(editor));
        Assert.Equal(2.0, Math.Round(_recorder.Marks.Single().Seconds, 2));
        // L'enregistrador hi posa una marca nova i reescriu el fitxer: la moguda s'hi manté.
        _input!.PushTone(1.0);
        _recorder.Mark();
        Assert.Equal([2.0, 4.0], RecordingMarks.Read(_recorder.FilePath!).Select(m => Math.Round(m.Seconds, 2)));
        // La nova és després de la fotografia: a l'editor no hi surt (fins que s'actualitzi).
        Assert.Equal([2.0], Seconds(Editor(Send("editor.get"))));
        _recorder.Stop();
    }

    [Fact]
    public void Si_passa_per_davant_d_una_altra_es_renumeren_igual_a_tot_arreu()
    {
        _recorder.Start();
        RecordAndWait(1.0, 0);
        _recorder.Mark();               // 1 a 1 s
        _input!.PushTone(1.0);
        _recorder.Mark();               // 2 a 2 s
        RecordAndWait(1.0, 2.5);
        Editor(Send("editor.open", new JsonObject { ["path"] = _recorder.FilePath! }));
        WaitIdle();

        var editor = Editor(Send("editor.moveMark", new JsonObject { ["number"] = 1, ["seconds"] = 2.5 }));

        // La que era a 2 s passa a ser la 1, a l'editor i a l'enregistrador.
        Assert.Equal([2.0, 2.5], Seconds(editor));
        Assert.Equal([1, 2], editor["marks"]!.AsArray().Select(m => m!["number"]!.GetValue<int>()));
        Assert.Equal([2.0, 2.5], _recorder.Marks.Select(m => Math.Round(m.Seconds, 2)));
        // Posar nom a la 2 des del panell de l'enregistrador: és la de 2,5 s.
        _recorder.NameMark(2, "Moguda");
        Assert.Equal("Moguda", RecordingMarks.Read(_recorder.FilePath!).Single(m => Math.Round(m.Seconds, 2) == 2.5).Name);
        _recorder.Stop();
    }

    [Fact]
    public void Amb_l_enregistrament_pendent_tambe_passa_per_l_enregistrador()
    {
        _recorder.Start();
        _input!.PushTone(1.0);
        _recorder.Mark();
        _input.PushTone(1.0);
        _recorder.Stop();
        Editor(Send("editor.open", new JsonObject { ["path"] = _recorder.FilePath! }));
        Assert.False(WaitIdle()["live"]!.GetValue<bool>());

        Editor(Send("editor.moveMark", new JsonObject { ["number"] = 1, ["seconds"] = 0.5 }));
        _recorder.NameMark(1, "Inici"); // abans, això tornava a escriure la marca on era

        var mark = RecordingMarks.Read(_recorder.FilePath!).Single();
        Assert.Equal(0.5, mark.Seconds, 2);
        Assert.Equal("Inici", mark.Name);
    }

    [Fact]
    public void Una_marca_no_es_pot_moure_mes_enlla_del_que_s_ha_enregistrat()
    {
        _recorder.Start();
        _input!.PushTone(1.0);
        _recorder.Mark();
        Assert.Throws<RecorderCommandException>(() => _recorder.MoveMark(2, 0.5));
        _recorder.MoveMark(1, 99);
        Assert.Equal(_recorder.Elapsed.TotalSeconds, _recorder.Marks.Single().Seconds, 2);
        _recorder.Stop();
    }

    [Fact]
    public void Un_enregistrament_interromput_continua_sense_obrir_se()
    {
        string path = Path.Combine(_folder, "interromput.mp3");
        File.WriteAllBytes(path, [0xFF, 0xFB, 0x90, 0x00]);
        File.WriteAllText(path + ".rec", "{}");

        Assert.Equal("bad_state", ErrorCode(Send("editor.open", new JsonObject { ["path"] = path })));
    }
}
