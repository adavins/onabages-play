using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>Forma d'ona de l'editor (fase 3, pas 2): càlcul, memòria cau i ordre editor.peaks.</summary>
public class WaveformTests : IDisposable
{
    private const int Rate = 48000;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "wave-" + Guid.NewGuid().ToString("N"));
    private string Cache => Path.Combine(_folder, "cache");

    public WaveformTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// MP3 amb 1 s de silenci i 1 s de to: esquerre a 0,5 i dret a 0,25 de l'escala.
    /// Així es pot comprovar on hi ha so i quant fort és cada canal.
    /// </summary>
    private string CreateSilenceThenTone(string name = "prova.mp3")
    {
        var samples = new float[Rate * 2 * 2];
        for (int i = Rate; i < Rate * 2; i++)
        {
            float sine = (float)Math.Sin(2 * Math.PI * 440 * i / Rate);
            samples[i * 2] = 0.5f * sine;
            samples[i * 2 + 1] = 0.25f * sine;
        }
        string path = Path.Combine(_folder, name);
        using var encoder = new LameEncoder(Rate, 320);
        using var file = File.Create(path);
        file.Write(encoder.Encode(samples, samples.Length));
        file.Write(encoder.Flush());
        return path;
    }

    private static int Left(byte[] peaks, int frame) => peaks[frame * 2];
    private static int Right(byte[] peaks, int frame) => peaks[frame * 2 + 1];

    [Fact]
    public void Els_pics_diuen_on_hi_ha_so_i_quant_fort()
    {
        var index = Mp3FrameIndex.Build(CreateSilenceThenTone());
        var peaks = new byte[index.FrameCount * 2];
        int lastProgress = 0;

        WaveformPeaks.Compute(index.Path, index.SampleRate, index.SamplesPerFrame, index.FrameCount, peaks, p => lastProgress = p);

        Assert.Equal(index.FrameCount, lastProgress);
        // Fotogrames de 24 ms: el to comença a ≈ 41,7. Amb marge pel retard del codificador.
        for (int f = 2; f < 38; f++)
            Assert.True(Left(peaks, f) < 5, $"fotograma {f}: hi hauria d'haver silenci ({Left(peaks, f)})");
        for (int f = 48; f < 80; f++)
        {
            Assert.InRange(Left(peaks, f), 115, 145);  // ≈ 0,5 × 255
            Assert.InRange(Right(peaks, f), 55, 75);   // ≈ 0,25 × 255
        }
    }

    [Fact]
    public void Muntar_la_forma_d_ona_d_una_versio_editada()
    {
        byte[] original = [.. Enumerable.Range(0, 20).Select(i => (byte)i)]; // 10 fotogrames

        byte[] result = WaveformPeaks.Splice(original, [new FrameRange(1, 2), new FrameRange(7, 1)]);

        Assert.Equal([2, 3, 4, 5, 14, 15], result);
    }

    // --- Memòria cau ---

    [Fact]
    public void La_memoria_cau_desa_i_llegeix()
    {
        var index = Mp3FrameIndex.Build(CreateSilenceThenTone());
        byte[] peaks = [.. Enumerable.Range(0, index.FrameCount * 2).Select(i => (byte)(i % 251))];

        PeakCacheFile.Write(index, peaks, Cache);

        Assert.Equal(peaks, PeakCacheFile.Read(index, Cache));
    }

    [Fact]
    public void Si_el_fitxer_canvia_la_memoria_cau_no_serveix()
    {
        string path = CreateSilenceThenTone();
        var index = Mp3FrameIndex.Build(path);
        PeakCacheFile.Write(index, new byte[index.FrameCount * 2], Cache);

        File.AppendAllText(path, "x");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        Assert.Null(PeakCacheFile.Read(Mp3FrameIndex.Build(path), Cache));
    }

    [Fact]
    public void Un_fitxer_de_cau_malmes_no_es_fa_servir()
    {
        var index = Mp3FrameIndex.Build(CreateSilenceThenTone());
        Directory.CreateDirectory(Cache);
        File.WriteAllText(PeakCacheFile.PathFor(index, Cache), "brossa");

        Assert.Null(PeakCacheFile.Read(index, Cache));
    }

    [Fact]
    public void S_esborren_les_formes_d_ona_velles()
    {
        Directory.CreateDirectory(Cache);
        string old = Path.Combine(Cache, "vella.obpk");
        string recent = Path.Combine(Cache, "recent.obpk");
        File.WriteAllText(old, "x");
        File.WriteAllText(recent, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-PeakCacheFile.KeepDays - 1));

        PeakCacheFile.DeleteOld(Cache);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    // --- Editor ---

    private static void WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 400 && !condition(); i++) Thread.Sleep(25);
        Assert.True(condition(), "No ha acabat a temps.");
    }

    [Fact]
    public void En_obrir_es_calcula_i_es_desa_a_la_cau()
    {
        string path = CreateSilenceThenTone();
        using var editor = new EditorSession(Cache);

        editor.Open(path);
        WaitFor(() => editor.Edit is not null && editor.PeaksDone == editor.Edit.Index.FrameCount);

        var index = editor.Edit!.Index;
        WaitFor(() => PeakCacheFile.Read(index, Cache) is not null);
        var (start, data) = editor.ReadPeaks(0, int.MaxValue);
        Assert.Equal(0, start);
        Assert.Equal(index.FrameCount * 2, data.Length);
        Assert.Null(editor.PeaksError);
    }

    [Fact]
    public void La_segona_vegada_surt_de_la_cau_de_seguida()
    {
        string path = CreateSilenceThenTone();
        var index = Mp3FrameIndex.Build(path);
        byte[] fake = Enumerable.Repeat((byte)7, index.FrameCount * 2).ToArray();
        PeakCacheFile.Write(index, fake, Cache);
        using var editor = new EditorSession(Cache);

        editor.Open(path);
        WaitFor(() => editor.Edit is not null);

        // Just en acabar d'obrir ja hi és tota, i és la de la cau (no s'ha descodificat).
        Assert.Equal(index.FrameCount, editor.PeaksDone);
        Assert.Equal(fake, editor.ReadPeaks(0, int.MaxValue).Data);
    }

    [Fact]
    public void En_desar_la_forma_d_ona_del_nou_es_munta_sense_descodificar()
    {
        string path = CreateSilenceThenTone();
        using var editor = new EditorSession(Cache);
        editor.Open(path);
        WaitFor(() => editor.Edit is not null && editor.PeaksDone == editor.Edit.Index.FrameCount);
        byte[] original = editor.ReadPeaks(0, int.MaxValue).Data;
        editor.Change(e => e.Delete(0, 30));
        string target = Path.Combine(_folder, "editat.mp3");

        editor.Save(target, overwrite: false);
        WaitFor(() => editor.Busy is null);

        Assert.Equal(target, editor.SavedPath);
        Assert.Equal(editor.Edit!.Index.FrameCount, editor.PeaksDone); // completa de seguida
        Assert.Equal(original[60..], editor.ReadPeaks(0, int.MaxValue).Data);
        Assert.Equal(original[60..], PeakCacheFile.Read(editor.Edit.Index, Cache)); // i ja és a la cau
    }

    [Fact]
    public void L_ordre_editor_peaks_dona_els_pics_en_base64()
    {
        string path = CreateSilenceThenTone();
        var state = new EngineState(editor: new EditorSession(Cache));
        var router = new CommandRouter(state, () => { }, () => []);
        state.Editor.Open(path);
        WaitFor(() => state.Editor.Edit is not null && state.Editor.PeaksDone == state.Editor.Edit.Index.FrameCount);

        var r = router.Handle("""{ "v": 1, "id": 1, "cmd": "editor.peaks", "from": 10, "count": 5 }""");
        var all = router.Handle("""{ "v": 1, "id": 2, "cmd": "editor.peaks" }""");
        var editor = router.Handle("""{ "v": 1, "id": 3, "cmd": "editor.get" }""")["result"]!["editor"]!;
        state.Editor.Dispose();

        var peaks = r["result"]!["peaks"]!;
        Assert.Equal(10, peaks["start"]!.GetValue<int>());
        Assert.Equal(5, peaks["count"]!.GetValue<int>());
        Assert.Equal(10, Convert.FromBase64String(peaks["data"]!.GetValue<string>()).Length);
        Assert.Equal(state.Editor.Edit!.Index.FrameCount, all["result"]!["peaks"]!["count"]!.GetValue<int>());
        Assert.Equal(state.Editor.Edit.Index.FrameCount, editor["peaksDone"]!.GetValue<int>());
    }

    [Fact]
    public void Sense_fitxer_obert_editor_peaks_dona_bad_state()
    {
        var router = new CommandRouter(new EngineState(editor: new EditorSession(Cache)), () => { }, () => []);

        var r = router.Handle("""{ "v": 1, "id": 1, "cmd": "editor.peaks" }""");

        Assert.Equal("bad_state", r["error"]!["code"]!.GetValue<string>());
    }
}
