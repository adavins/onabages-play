using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>
/// Pas 7 de la fase 2: recuperació d'enregistraments interromputs (especificació 8.5).
/// Es fabrica un «enregistrament tallat» com el que deixaria una caiguda: MP3 amb un
/// fotograma a mitges al final i el marcador .rec al costat.
/// </summary>
public class RecoveryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "recov-" + Guid.NewGuid().ToString("N"));

    public RecoveryTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>MP3 de <paramref name="seconds"/> segons de to (48 kHz, 320 kbps, sense etiqueta Xing).</summary>
    private static byte[] Mp3Tone(double seconds)
    {
        var bytes = new MemoryStream();
        using var encoder = new LameEncoder(48000);
        var tone = new float[(int)(48000 * seconds) * 2];
        for (int i = 0; i < tone.Length; i++) tone[i] = 0.3f * (float)Math.Sin(i * 0.03);
        bytes.Write(encoder.Encode(tone, tone.Length));
        bytes.Write(encoder.Flush());
        return bytes.ToArray();
    }

    /// <summary>Crea un enregistrament interromput: MP3 + mig fotograma de més + marcador.</summary>
    private string Interrupted(string name = "Programa_2026-11-04_10-00-00.mp3", int extraBytes = 500,
        string recorder = "2", string startedAt = "2026-11-04T10:00:00.0000000+01:00")
    {
        string path = Path.Combine(_folder, name);
        var mp3 = Mp3Tone(2);
        var extra = mp3.AsSpan(0, extraBytes).ToArray(); // comença com un fotograma, però tallat
        File.WriteAllBytes(path, [.. mp3, .. extra]);
        File.WriteAllText(path + ".rec", new JsonObject
        {
            ["schemaVersion"] = 1, ["file"] = name, ["startedAt"] = startedAt, ["recorder"] = recorder,
        }.ToJsonString());
        return path;
    }

    // --- Recuperar un fitxer ---

    [Fact]
    public void Retalla_fins_a_l_ultim_fotograma_complet_i_esborra_el_marcador()
    {
        string path = Interrupted(extraBytes: 500);
        long before = new FileInfo(path).Length;

        var result = RecordingRecovery.Recover(path + ".rec"); // també accepta el marcador

        Assert.Equal(RecoveryOutcome.Recovered, result.Outcome);
        Assert.Equal(path, result.Path);
        Assert.Equal(500, result.TrimmedBytes);
        Assert.Equal(before - 500, new FileInfo(path).Length);
        Assert.False(File.Exists(path + ".rec"));
        Assert.Equal("2", result.RecorderId);
        Assert.Equal(DateTimeOffset.Parse("2026-11-04T10:00:00+01:00"), result.StartedAt);
        Assert.InRange(result.Duration.TotalSeconds, 1.9, 2.2);

        // El fitxer és vàlid: no queda res tallat i es pot llegir sencer.
        var analysis = Mp3Frames.Analyze(path);
        Assert.Equal(0, analysis.TrailingBytes);
        using var source = AudioFileSource.Open(path, 48000);
        Assert.True(TestAudio.ReadAll(source).Length > 48000);
    }

    [Fact]
    public void Un_fitxer_sencer_es_recupera_sense_tocar_l_audio()
    {
        string path = Interrupted(extraBytes: 0);
        long before = new FileInfo(path).Length;

        var result = RecordingRecovery.Recover(path);

        Assert.Equal(RecoveryOutcome.Recovered, result.Outcome);
        Assert.Equal(0, result.TrimmedBytes);
        Assert.Equal(before, new FileInfo(path).Length);
        Assert.False(File.Exists(path + ".rec"));
    }

    [Fact]
    public void Treu_l_etiqueta_xing_si_n_hi_ha()
    {
        string path = Interrupted(extraBytes: 0);
        var original = File.ReadAllBytes(path);
        var analysis = Mp3Frames.Analyze(original);

        // Afegeix al principi un fotograma amb «Info» (com el que escriu LAME si no es desactiva).
        var tag = original.AsSpan(0, analysis.FirstFrameLength).ToArray();
        "Info"u8.CopyTo(tag.AsSpan(36));
        File.WriteAllBytes(path, [.. tag, .. original]);
        Assert.True(Mp3Frames.Analyze(path).HasXingTag);

        var result = RecordingRecovery.Recover(path);

        Assert.Equal(RecoveryOutcome.Recovered, result.Outcome);
        Assert.True(result.RemovedTag);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void No_toca_un_fitxer_que_encara_s_esta_enregistrant()
    {
        FakeInput? input = null;
        using var recorder = new Recorder("1", _ => input = new FakeInput());
        recorder.SetDevice("{0.0.1.00000000}.{proves}");
        recorder.Configure(folder: _folder);
        recorder.Start();
        input!.PushTone(1);
        string path = recorder.FilePath!;

        var result = RecordingRecovery.Recover(path);

        Assert.Equal(RecoveryOutcome.InUse, result.Outcome);
        Assert.True(File.Exists(path + ".rec"));

        // En aturar-lo bé, el marcador desapareix com sempre.
        recorder.Stop();
        Assert.False(File.Exists(path + ".rec"));
    }

    [Fact]
    public void Si_el_mp3_no_hi_es_s_esborra_el_marcador()
    {
        string path = Interrupted();
        File.Delete(path);

        var result = RecordingRecovery.Recover(path + ".rec");

        Assert.Equal(RecoveryOutcome.Missing, result.Outcome);
        Assert.False(File.Exists(path + ".rec"));
    }

    [Fact]
    public void Si_no_hi_ha_cap_fotograma_no_es_toca_res()
    {
        string path = Path.Combine(_folder, "Malmes.mp3");
        File.WriteAllBytes(path, new byte[5000]); // zeros: res de reconeixible
        File.WriteAllText(path + ".rec", "{}");

        var result = RecordingRecovery.Recover(path);

        Assert.Equal(RecoveryOutcome.Failed, result.Outcome);
        Assert.Equal(5000, new FileInfo(path).Length);
        Assert.True(File.Exists(path + ".rec"));
    }

    [Fact]
    public void Un_marcador_malmes_no_impedeix_recuperar()
    {
        string path = Interrupted();
        File.WriteAllText(path + ".rec", ""); // tall de corrent just en crear-lo

        var result = RecordingRecovery.Recover(path);

        Assert.Equal(RecoveryOutcome.Recovered, result.Outcome);
        Assert.Equal("", result.RecorderId);
        Assert.False(File.Exists(path + ".rec"));
    }

    // --- En arrencar el motor ---

    [Fact]
    public void En_arrencar_es_recuperen_i_surten_a_recents_com_a_recuperats()
    {
        string older = Interrupted("A_2026-11-04_09-00-00.mp3", startedAt: "2026-11-04T09:00:00+01:00");
        string newer = Interrupted("B_2026-11-04_11-00-00.mp3", startedAt: "2026-11-04T11:00:00+01:00");
        var list = new RecordingList(synchronous: true);
        // Un de la llista anterior, de les 10:00 (entre els dos).
        list.AddLoaded([new RecordingItem("", Path.Combine(_folder, "Desat.mp3"), "1",
            DateTimeOffset.Parse("2026-11-04T10:00:00+01:00"), TimeSpan.FromSeconds(5), 1000, true, false, true, null)]);

        var results = StartupRecovery.Run([_folder], list);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(RecoveryOutcome.Recovered, r.Outcome));
        var items = list.Items;
        Assert.Equal([newer, Path.Combine(_folder, "Desat.mp3"), older], items.Select(i => i.Path));
        Assert.True(items[0].Recovered);
        Assert.False(items[0].ClosedCleanly);
        Assert.False(items[1].Recovered);

        // El protocol ho diu, i es desa per a la propera vegada.
        Assert.True(RecordingJson.Item(items[0])["recovered"]!.GetValue<bool>());
        string file = Path.Combine(_folder, "recents.json");
        RecentRecordingsFile.Write(items, file);
        Assert.True(RecentRecordingsFile.Read(file)[0].Recovered);
    }

    [Fact]
    public void Si_ja_era_a_la_llista_s_hi_actualitza_sense_duplicar_lo()
    {
        string path = Interrupted();
        var list = new RecordingList(synchronous: true);
        // Es va tallar (p. ex. disc ple) i ja era a la llista, amb la durada d'aleshores.
        list.Add(new FinishedRecording("2", path, DateTimeOffset.Now, TimeSpan.FromSeconds(1), 10, ClosedCleanly: false));

        StartupRecovery.Run([_folder], list);

        var item = Assert.Single(list.Items);
        Assert.True(item.Recovered);
        Assert.InRange(item.Duration.TotalSeconds, 1.9, 2.2);
        Assert.Equal(new FileInfo(path).Length, item.FileSize);
    }

    [Fact]
    public void Es_busca_a_les_carpetes_dels_enregistradors_i_de_la_llista_sense_repetir()
    {
        using var one = new Recorder("1");
        using var two = new Recorder("2");
        one.Configure(folder: _folder);
        two.Configure(folder: _folder + "\\");
        var list = new RecordingList(synchronous: true);
        string other = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "altra");
        list.Add(new FinishedRecording("1", Path.Combine(other, "x.mp3"), DateTimeOffset.Now, TimeSpan.Zero, 0, true));

        var folders = StartupRecovery.FoldersToScan([one, two], list);

        Assert.Equal([_folder, Recorder.DefaultFolder, other], folders);
    }
}
