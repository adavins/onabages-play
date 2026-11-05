using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>
/// Fosos a l'editor (fase 4, pas 6b; D-074): al model (amb desfer i refer), en desar (sense recodificar:
/// la mida no canvia i el resultat es descodifica i es mesura), a la previsualització, a la forma d'ona
/// i a les ordres editor.fadeIn i editor.fadeOut.
/// </summary>
public class EditorFadeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "fosos-" + Guid.NewGuid().ToString("N"));

    public EditorFadeTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>MP3 amb un to de 1 kHz a −12 dBFS.</summary>
    private string Tone(double seconds, int rate = 48000, int kbps = 320)
    {
        string path = Path.Combine(_folder, $"to-{Guid.NewGuid():N}.mp3");
        using var encoder = new LameEncoder(rate, kbps);
        using var file = File.Create(path);
        var block = new float[rate / 10 * 2];
        float amplitude = (float)Math.Pow(10, -12 / 20.0);
        for (int n = 0; n < seconds * 10; n++)
        {
            for (int f = 0; f < block.Length / 2; f++)
                block[f * 2] = block[f * 2 + 1] = amplitude * (float)Math.Sin(2 * Math.PI * 1000 * (n * block.Length / 2 + f) / rate);
            file.Write(encoder.Encode(block, block.Length));
        }
        file.Write(encoder.Flush());
        return path;
    }

    private string SavePath() => Path.Combine(_folder, $"desat-{Guid.NewGuid():N}.mp3");

    /// <summary>Nivell RMS (dB) d'un tros del fitxer (descodificat a 48 kHz).</summary>
    private static double RmsDb(string path, double from, double length)
    {
        using var source = AudioFileSource.Open(path, 48000, TimeSpan.FromSeconds(from));
        var buffer = new float[(int)(48000 * length) * 2];
        int total = 0, read;
        while (total < buffer.Length && (read = source.Read(buffer.AsSpan(total))) > 0) total += read;
        double sum = 0;
        for (int i = 0; i < total; i++) sum += buffer[i] * buffer[i];
        return total == 0 ? double.NegativeInfinity : 10 * Math.Log10(Math.Max(sum / total, 1e-20));
    }

    // --- Model ---

    [Fact]
    public void Un_fos_de_sortida_baixa_fins_al_silenci_i_es_pot_desfer()
    {
        var edit = new Mp3Edit(Mp3FrameIndex.Build(Tone(3)));
        int end = edit.FrameCount, start = end - edit.FrameAt(1.0);

        Assert.True(edit.FadeOut(start, end));

        Assert.True(edit.IsModified);
        // Els primers fotogrames d'un fos de sortida encara no s'abaixen (0 passos) i no es guarden.
        Assert.InRange(edit.Gains.Count(g => g.Key >= start), end - start - 5, end - start);
        Assert.Equal(Mp3Edit.SilenceSteps, edit.Gains[end - 1].Granule1);   // l'últim grànul, en silenci
        Assert.InRange(edit.Gains.GetValueOrDefault(start).Granule0, -1, 0); // el primer, gairebé sense canvi (potser ni guardat)
        Assert.True(edit.Undo());
        Assert.Empty(edit.Gains);
        Assert.False(edit.IsModified);
        Assert.True(edit.Redo());
        Assert.NotEmpty(edit.Gains);
    }

    [Fact]
    public void Sense_seleccio_no_es_pot_fer_un_fos()
    {
        var edit = new Mp3Edit(Mp3FrameIndex.Build(Tone(1)));

        Assert.Throws<Mp3EditException>(() => edit.FadeIn(10, 10));
    }

    // --- Desar (sense recodificar) ---

    [Fact]
    public void Desar_un_fos_de_sortida_no_canvia_la_mida_i_sona_com_toca()
    {
        string original = Tone(4);
        var edit = new Mp3Edit(Mp3FrameIndex.Build(original));
        edit.FadeOut(edit.FrameAt(2.0), edit.FrameCount); // de 2 s al final (4 s)
        string saved = SavePath();

        edit.Save(saved);

        Assert.Equal(new FileInfo(original).Length, new FileInfo(saved).Length);
        double full = RmsDb(original, 0.5, 1);
        Assert.InRange(RmsDb(saved, 0.5, 1) - full, -0.1, 0.1);        // abans del fos, igual
        Assert.InRange(RmsDb(saved, 2.95, 0.1) - full, -7.5, -4.5);    // a la meitat del fos, la meitat d'amplitud (−6 dB)
        Assert.True(RmsDb(saved, 3.9, 0.08) - full < -25);             // al final, gairebé silenci
    }

    [Fact]
    public void Desar_un_fos_dentrada()
    {
        string original = Tone(3);
        var edit = new Mp3Edit(Mp3FrameIndex.Build(original));
        edit.FadeIn(0, edit.FrameAt(1.0));
        string saved = SavePath();

        edit.Save(saved);

        double full = RmsDb(original, 2, 0.5);
        Assert.True(RmsDb(saved, 0.03, 0.05) - full < -20);            // al principi, gairebé silenci
        Assert.InRange(RmsDb(saved, 2, 0.5) - full, -0.1, 0.1);        // després del fos, igual
    }

    [Fact]
    public void El_fos_es_mou_amb_el_seu_tros_si_despres_es_talla()
    {
        string original = Tone(4);
        var edit = new Mp3Edit(Mp3FrameIndex.Build(original));
        edit.FadeOut(edit.FrameAt(3.0), edit.FrameCount);  // l'últim segon
        edit.Delete(0, edit.FrameAt(1.0));                 // es treu el primer segon
        string saved = SavePath();

        edit.Save(saved);

        double full = RmsDb(original, 0.5, 1);
        Assert.InRange(RmsDb(saved, 0.5, 1) - full, -0.1, 0.1);        // ara de 0 a 2 s, sense fos
        // El fos és al final (de 2 a 3 s): al 90 % del fos queda un 10 % d'amplitud (−20 dB).
        Assert.True(RmsDb(saved, 2.9, 0.08) - full < -15);
    }

    [Fact]
    public void Dos_fosos_al_mateix_lloc_se_sumen()
    {
        var edit = new Mp3Edit(Mp3FrameIndex.Build(Tone(2)));
        int start = 0, end = edit.FrameAt(1.0), middle = end / 2;
        edit.FadeOut(start, end);
        int once = edit.Gains[middle].Granule0;

        edit.FadeOut(start, end);

        Assert.InRange(edit.Gains[middle].Granule0, 2 * once - 1, 2 * once + 1);
    }

    [Fact]
    public void Tambe_amb_MPEG_2_un_granul_per_fotograma()
    {
        string original = Tone(3, rate: 22050, kbps: 64);
        var edit = new Mp3Edit(Mp3FrameIndex.Build(original));
        Assert.Equal(1, edit.GranulesPerFrame);
        edit.FadeOut(edit.FrameAt(2.0), edit.FrameCount);
        string saved = SavePath();

        edit.Save(saved);

        Assert.True(RmsDb(saved, 2.9, 0.06) - RmsDb(original, 0.5, 1) < -15); // al 90 % del fos (−20 dB)
    }

    // --- Previsualització i forma d'ona ---

    [Fact]
    public void La_previsualitzacio_sap_el_guany_de_cada_punt()
    {
        var edit = new Mp3Edit(Mp3FrameIndex.Build(Tone(3)));
        edit.FadeOut(edit.FrameAt(2.0), edit.FrameCount);

        Assert.Equal(1f, edit.GainAt(1.0));                 // abans del fos
        Assert.InRange(edit.GainAt(2.5), 0.4f, 0.6f);       // a la meitat
        Assert.True(edit.GainAt(edit.Duration.TotalSeconds - 0.005) < 0.01f); // al final
    }

    [Fact]
    public void La_forma_dona_del_fitxer_desat_porta_el_fos()
    {
        var peaks = Enumerable.Repeat((byte)200, 10 * WaveformPeaks.BytesPerFrame).ToArray();
        var gains = new Dictionary<int, FrameGain> { [5] = new(-4, -4), [9] = new(Mp3Edit.SilenceSteps, Mp3Edit.SilenceSteps) };

        var result = WaveformPeaks.Splice(peaks, [new FrameRange(0, 10)], gains);

        Assert.Equal(200, result[4 * 2]);
        Assert.Equal(100, result[5 * 2]);  // −6 dB: la meitat
        Assert.Equal(0, result[9 * 2]);
    }

    // --- Ordres ---

    [Fact]
    public void Ordres_fadeIn_i_fadeOut()
    {
        var state = new EngineState([], [], editor: new EditorSession(Path.Combine(_folder, "cache")));
        var router = new CommandRouter(state, () => { }, () => []);
        string path = Tone(2);
        router.Handle($$"""{ "v": 1, "id": 1, "cmd": "editor.open", "path": "{{path.Replace("\\", "\\\\")}}" }""");
        for (int i = 0; i < 200 && state.Editor.Busy is not null; i++) Thread.Sleep(25);

        var r = router.Handle("""{ "v": 1, "id": 2, "cmd": "editor.fadeOut", "start": 1, "end": 2 }""");
        var editor = r["result"]!["editor"]!;
        Assert.True(r["result"]!["changed"]!.GetValue<bool>());
        Assert.True(editor["modified"]!.GetValue<bool>());
        Assert.True(editor["gains"]!.AsArray().Count > 30);
        Assert.Equal(Mp3Edit.SilenceSteps, editor["silenceSteps"]!.GetValue<int>());
        Assert.Equal(2, editor["granulesPerFrame"]!.GetValue<int>());

        r = router.Handle("""{ "v": 1, "id": 3, "cmd": "editor.fadeIn", "start": 1, "end": 1 }""");
        Assert.Equal("bad_state", r["error"]!["code"]!.GetValue<string>());

        state.Editor.Dispose();
    }
}
