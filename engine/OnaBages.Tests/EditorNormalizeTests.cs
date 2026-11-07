using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>
/// Normalització per pic a l'editor (fase 5, pas 26a; D-123): el pic arriba a l'objectiu sense passar-lo, en passos
/// de 1,5 dB i sense recodificar; selecció o tot el fitxer; es pot desfer; l'objectiu es recorda.
/// </summary>
public class EditorNormalizeTests : IDisposable
{
    private const int Rate = 48000;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "norm-" + Guid.NewGuid().ToString("N"));
    private readonly EngineState _state;
    private readonly CommandRouter _router;
    private readonly ConfigRepository _repository;
    private int _id;

    public EditorNormalizeTests()
    {
        Directory.CreateDirectory(_folder);
        _repository = new ConfigRepository(Path.Combine(_folder, "dades", "config.json"));
        var store = new ConfigStore(_repository);
        store.Load();
        _state = new EngineState([], [], editor: new EditorSession(Path.Combine(_folder, "cache")));
        _router = new CommandRouter(_state, () => { }, () => [], config: store);
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

    private static JsonObject Ok(JsonObject r)
    {
        Assert.True(r["ok"]!.GetValue<bool>(), r.ToJsonString());
        return r["result"]!.AsObject();
    }

    /// <summary>
    /// MP3 amb un to de 1 kHz: <paramref name="firstDb"/> fins a la meitat i <paramref name="secondDb"/> després (dBFS de pic).
    /// </summary>
    private string Tone(double seconds, double firstDb, double secondDb)
    {
        string path = Path.Combine(_folder, $"to-{Guid.NewGuid():N}.mp3");
        using var encoder = new LameEncoder(Rate, 320);
        using var file = File.Create(path);
        var block = new float[Rate / 10 * 2];
        for (int n = 0; n < seconds * 10; n++)
        {
            float amplitude = (float)Math.Pow(10, (n < seconds * 5 ? firstDb : secondDb) / 20);
            for (int f = 0; f < block.Length / 2; f++)
                block[f * 2] = block[f * 2 + 1] = amplitude * (float)Math.Sin(2 * Math.PI * 1000 * (n * block.Length / 2 + f) / Rate);
            file.Write(encoder.Encode(block, block.Length));
        }
        file.Write(encoder.Flush());
        return path;
    }

    /// <summary>Obre el fitxer i espera que acabi la forma d'ona.</summary>
    private void Open(string path)
    {
        Ok(Send("editor.open", new JsonObject { ["path"] = path }));
        for (int i = 0; i < 400; i++)
        {
            var e = Ok(Send("editor.get"))["editor"]!;
            if (e["busy"] is null && e["peaksDone"]!.GetValue<int>() == e["originalFrames"]!.GetValue<int>()) return;
            Thread.Sleep(25);
        }
        throw new TimeoutException("La forma d'ona no ha acabat a temps.");
    }

    /// <summary>Pic (dBFS) d'un tros del fitxer descodificat.</summary>
    private static double PeakDb(string path, double from, double length)
    {
        using var source = AudioFileSource.Open(path, Rate, TimeSpan.FromSeconds(from));
        var buffer = new float[(int)(Rate * length) * 2];
        int total = 0, read;
        while (total < buffer.Length && (read = source.Read(buffer.AsSpan(total))) > 0) total += read;
        float peak = 0;
        for (int i = 0; i < total; i++) peak = Math.Max(peak, Math.Abs(buffer[i]));
        return 20 * Math.Log10(peak);
    }

    private string SaveAs()
    {
        string target = Path.Combine(_folder, $"desat-{Guid.NewGuid():N}.mp3");
        Ok(Send("editor.save", new JsonObject { ["path"] = target }));
        for (int i = 0; i < 400 && Ok(Send("editor.get"))["editor"]!["busy"] is not null; i++) Thread.Sleep(25);
        return target;
    }

    [Fact]
    public void Tot_el_fitxer_puja_fins_a_l_objectiu_sense_passar_lo()
    {
        string path = Tone(2, -12, -12);
        Open(path);

        // Previsualització: no canvia res.
        var preview = Ok(Send("editor.normalize", new JsonObject { ["preview"] = true }));
        var plan = preview["normalize"]!;
        Assert.Equal(-1, plan["targetDb"]!.GetValue<double>());          // per defecte
        Assert.InRange(plan["peakDb"]!.GetValue<double>(), -12.3, -11.5);
        Assert.Equal(10.5, plan["gainDb"]!.GetValue<double>());          // 7 passos: −12 + 10,5 = −1,5
        Assert.True(plan["whole"]!.GetValue<bool>());
        Assert.False(preview["changed"]!.GetValue<bool>());
        Assert.False(preview["editor"]!["modified"]!.GetValue<bool>());

        var applied = Ok(Send("editor.normalize", new JsonObject { ["targetDb"] = -1 }));
        Assert.True(applied["changed"]!.GetValue<bool>());
        Assert.True(applied["editor"]!["canUndo"]!.GetValue<bool>());

        long size = new FileInfo(path).Length;
        string saved = SaveAs();
        Assert.Equal(size, new FileInfo(saved).Length);                 // sense recodificar
        double peak = PeakDb(saved, 0.2, 1.6);
        Assert.InRange(peak, -2.0, -1.0);                                // a prop de l'objectiu i sense passar-lo
    }

    [Fact]
    public void Nomes_la_seleccio_i_es_pot_desfer()
    {
        Open(Tone(4, -6, -18));                                          // la segona meitat, 12 dB més fluixa

        var r = Ok(Send("editor.normalize", new JsonObject { ["start"] = 2.2, ["end"] = 4.0, ["targetDb"] = -6 }));
        Assert.False(r["normalize"]!["whole"]!.GetValue<bool>());
        Assert.Equal(10.5, r["normalize"]!["gainDb"]!.GetValue<double>());
        var gains = r["editor"]!["gains"]!.AsArray();
        Assert.NotEmpty(gains);
        double firstGainSeconds = gains[0]![0]!.GetValue<int>() * 1152.0 / Rate;
        Assert.InRange(firstGainSeconds, 2.15, 2.25);                    // abans de la selecció no es toca

        string saved = SaveAs();
        Assert.InRange(PeakDb(saved, 0.2, 1.6), -6.5, -5.5);
        Assert.InRange(PeakDb(saved, 2.4, 1.4), -8.0, -6.0);

        Ok(Send("editor.undo"));
        Assert.Empty(Ok(Send("editor.get"))["editor"]!["gains"]!.AsArray());
    }

    [Fact]
    public void Si_passa_de_l_objectiu_baixa()
    {
        Open(Tone(2, -1, -1));
        var plan = Ok(Send("editor.normalize", new JsonObject { ["targetDb"] = -6, ["preview"] = true }))["normalize"]!;
        Assert.Equal(-6.0, plan["gainDb"]!.GetValue<double>());
        Assert.True(plan["resultPeakDb"]!.GetValue<double>() <= -6);
    }

    [Fact]
    public void Ja_hi_es_no_canvia_res()
    {
        Open(Tone(2, -1.5, -1.5));
        var r = Ok(Send("editor.normalize", new JsonObject { ["targetDb"] = -1 }));
        Assert.Equal(0, r["normalize"]!["gainDb"]!.GetValue<double>());
        Assert.False(r["changed"]!.GetValue<bool>());
        Assert.False(r["editor"]!["canUndo"]!.GetValue<bool>());
    }

    [Fact]
    public void Molt_fluix_es_limita_a_24_dB()
    {
        Open(Tone(2, -50, -50));
        var plan = Ok(Send("editor.normalize", new JsonObject { ["preview"] = true }))["normalize"]!;
        Assert.Equal(24, plan["gainDb"]!.GetValue<double>());
        Assert.True(plan["limited"]!.GetValue<bool>());
    }

    [Fact]
    public void L_objectiu_es_recorda_i_els_errors()
    {
        Assert.Equal("bad_state", Send("editor.normalize")["error"]!["code"]!.GetValue<string>()); // sense fitxer

        Open(Tone(2, -12, -12));
        Assert.Equal("bad_params", Send("editor.normalize", new JsonObject { ["targetDb"] = 1 })["error"]!["code"]!.GetValue<string>());
        Assert.Equal("bad_params", Send("editor.normalize", new JsonObject { ["targetDb"] = -20 })["error"]!["code"]!.GetValue<string>());

        Ok(Send("editor.normalize", new JsonObject { ["targetDb"] = -3 }));
        Assert.Equal(-3, _repository.Load().Config.EditorNormalizeTargetDb);
        var plan = Ok(Send("editor.normalize", new JsonObject { ["preview"] = true }))["normalize"]!;
        Assert.Equal(-3, plan["targetDb"]!.GetValue<double>());
    }

    [Fact]
    public void La_forma_d_ona_amb_guany_no_desborda()
    {
        byte[] peaks = [200, 100];
        var gains = new Dictionary<int, FrameGain> { [0] = new FrameGain(4, 4) };   // +6 dB
        var result = WaveformPeaks.Splice(peaks, [new FrameRange(0, 1)], gains);
        Assert.Equal(255, result[0]);                                     // abans: 399 → 143
        Assert.InRange(result[1], 198, 201);
    }

    [Fact]
    public void Model_el_guany_es_suma_als_fosos_i_el_silenci_continua_sent_silenci()
    {
        string path = Tone(1, -12, -12);
        var edit = new Mp3Edit(Mp3FrameIndex.Build(path));
        int frames = edit.FrameCount;
        edit.FadeOut(frames - 10, frames);
        var before = edit.Gains;
        edit.Gain(0, frames, 2);
        Assert.Equal(2, edit.Gains[0].Granule0);
        var last = before[edit.ToOriginal(frames - 1)];
        Assert.Equal(last.Granule1 <= Mp3Edit.SilenceSteps ? last.Granule1 : last.Granule1 + 2, edit.Gains[edit.ToOriginal(frames - 1)].Granule1);
        Assert.Equal(before[edit.ToOriginal(frames - 5)].Granule0 + 2, edit.Gains[edit.ToOriginal(frames - 5)].Granule0);
        Assert.Throws<Mp3EditException>(() => edit.Gain(3, 3, 1));
        Assert.False(edit.Gain(0, frames, 0));
    }
}
