using NAudio.Wave;
using OnaBages.Audio;

namespace OnaBages.Tests;

/// <summary>Codificació MP3 i enregistrador, amb una entrada falsa (sense targeta de so).</summary>
public class RecorderTests
{
    private const string DeviceId = "{0.0.1.00000000}.{proves}";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "rec-" + Guid.NewGuid().ToString("N"));
    private FakeInput? _input;

    private Recorder NewRecorder(int sampleRate = 48000, int channels = 2)
    {
        var recorder = new Recorder("1", _ => _input = new FakeInput(sampleRate, channels));
        recorder.SetDevice(DeviceId);
        return recorder;
    }

    /// <summary>
    /// Fotogrames que ha de tenir un MP3 de <paramref name="frames"/> mostres per canal:
    /// LAME hi afegeix un retard inicial (~1105 mostres) i completa l'últim fotograma.
    /// </summary>
    private static void AssertFrameCount(int mp3Frames, long frames)
    {
        long min = frames / 1152;
        long max = (frames + 1105 + 2 * 1152) / 1152 + 1;
        Assert.InRange(mp3Frames, min, max);
    }

    // --- LAME ---

    [Fact]
    public void LAME_es_la_versio_3_100()
    {
        Assert.StartsWith("3.100", LameEncoder.Version);
    }

    [Fact]
    public void LAME_fa_mp3_de_320_kbps_cbr_sense_etiqueta_xing()
    {
        var bytes = new MemoryStream();
        using (var encoder = new LameEncoder(48000))
        {
            var tone = new float[48000 * 2 * 2]; // 2 s estèreo
            for (int i = 0; i < tone.Length; i++) tone[i] = 0.3f * (float)Math.Sin(i * 0.03);
            bytes.Write(encoder.Encode(tone, tone.Length));
            bytes.Write(encoder.Flush());
        }

        var analysis = Mp3Frames.Analyze(bytes.ToArray());

        Assert.Equal([320], analysis.Bitrates);
        Assert.Equal(48000, analysis.SampleRate);
        Assert.False(analysis.HasXingTag);
        Assert.Equal(0, analysis.TrailingBytes);
        AssertFrameCount(analysis.Frames, 96000);
    }

    [Fact]
    public void Un_mp3_tallat_es_llegeix_fins_a_l_ultim_fotograma_complet()
    {
        var bytes = new MemoryStream();
        using (var encoder = new LameEncoder(44100))
        {
            var tone = new float[44100 * 2];
            for (int i = 0; i < tone.Length; i++) tone[i] = 0.3f * (float)Math.Sin(i * 0.03);
            bytes.Write(encoder.Encode(tone, tone.Length));
            bytes.Write(encoder.Flush());
        }
        var whole = Mp3Frames.Analyze(bytes.ToArray());

        // Com si el procés hagués caigut a mig fotograma.
        byte[] cut = bytes.ToArray()[..(int)(whole.ValidLength - 300)];
        var analysis = Mp3Frames.Analyze(cut);

        Assert.Equal(whole.Frames - 1, analysis.Frames);
        Assert.True(analysis.TrailingBytes > 0);

        // I un reproductor el pot obrir i llegir sense errors.
        string path = Path.Combine(Path.GetTempPath(), $"tallat-{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(path, cut);
        using var source = AudioFileSource.Open(path, 44100);
        Assert.True(TestAudio.ReadAll(source).Length > 0);
    }

    // --- Enregistrador ---

    [Fact]
    public void Enregistra_un_fitxer_mp3_valid_i_esborra_el_marcador()
    {
        using var recorder = NewRecorder();

        recorder.Start(_folder, "Programa");

        Assert.Equal(RecorderState.Recording, recorder.State);
        string path = recorder.FilePath!;
        Assert.StartsWith(Path.Combine(_folder, "Programa_"), path);
        Assert.EndsWith(".mp3", path);
        Assert.True(File.Exists(path + ".rec"), "Mentre s'enregistra hi ha d'haver el marcador .rec");

        _input!.PushTone(3.0);
        recorder.Stop();

        Assert.Equal(RecorderState.Pending, recorder.State);
        Assert.False(File.Exists(path + ".rec"), "En aturar bé, el marcador s'esborra");
        Assert.Equal(3.0, recorder.Elapsed.TotalSeconds, 3);
        Assert.Equal(0, recorder.SamplesLost);
        Assert.Equal(new FileInfo(path).Length, recorder.FileSize);

        var analysis = Mp3Frames.Analyze(path);
        Assert.Equal([320], analysis.Bitrates);
        Assert.Equal(48000, analysis.SampleRate);
        Assert.False(analysis.HasXingTag);
        Assert.Equal(0, analysis.TrailingBytes);
        AssertFrameCount(analysis.Frames, 3 * 48000);
    }

    [Fact]
    public void Entrada_mono_a_44100_dona_mp3_estereo_a_44100()
    {
        using var recorder = NewRecorder(44100, 1);
        recorder.Start(_folder, "Mono");
        _input!.PushTone(1.0);
        recorder.Stop();

        using var reader = new MediaFoundationReader(recorder.FilePath!);
        Assert.Equal(2, reader.WaveFormat.Channels);
        Assert.Equal(44100, reader.WaveFormat.SampleRate);
        Assert.Equal(44100, Mp3Frames.Analyze(recorder.FilePath!).SampleRate);
    }

    [Fact]
    public void Paquets_marcats_com_a_silenci_s_enregistren_com_a_silenci()
    {
        using var recorder = NewRecorder();
        recorder.Start(_folder, "Silenci");
        var loud = Enumerable.Repeat(0.9f, 4800 * 2).ToArray();
        for (int i = 0; i < 100; i++) _input!.Push(loud, silent: true); // 10 s «silenciosos»
        recorder.Stop();

        Assert.Equal(10.0, recorder.Elapsed.TotalSeconds, 3);
        using var source = AudioFileSource.Open(recorder.FilePath!, 48000);
        Assert.True(TestAudio.ReadAll(source).Max(Math.Abs) < 0.01f);
    }

    [Fact]
    public void Avisos_de_dades_perdudes_de_windows_es_compten()
    {
        using var recorder = NewRecorder();
        recorder.Start(_folder, "Glitch");
        _input!.Push(new float[960], discontinuity: true); // primer paquet: Windows sempre el marca, no compta
        _input.Push(new float[960]);
        _input.Push(new float[960], discontinuity: true);  // aquest sí que és un tall
        recorder.Stop();

        Assert.Equal(1, recorder.CaptureGlitches);
    }

    [Fact]
    public void Entrada_desconnectada_tanca_el_fitxer_net_i_passa_a_error()
    {
        using var recorder = NewRecorder();
        recorder.Start(_folder, "Desconnexio");
        string path = recorder.FilePath!;
        _input!.PushTone(2.0);

        _input.Fail("device invalidated");
        recorder.Tick();

        Assert.Equal(RecorderState.Error, recorder.State);
        Assert.Contains("Entrada no disponible", recorder.ErrorMessage);
        Assert.Contains("desat fins aquí", recorder.ErrorMessage);
        Assert.Equal("device invalidated", recorder.ErrorDetail);
        Assert.Equal(path, recorder.FilePath);
        Assert.False(File.Exists(path + ".rec"));
        var analysis = Mp3Frames.Analyze(path);
        Assert.Equal(0, analysis.TrailingBytes);
        AssertFrameCount(analysis.Frames, 2 * 48000);

        // «Continuar en un fitxer nou».
        recorder.Start(_folder, "Desconnexio");
        Assert.Equal(RecorderState.Recording, recorder.State);
        Assert.NotEqual(path, recorder.FilePath);
        recorder.Stop();
    }

    [Fact]
    public void Nou_enregistrament_des_de_pendent_fa_un_fitxer_nou()
    {
        using var recorder = NewRecorder();
        recorder.Start(_folder, "Programa");
        _input!.PushTone(0.5);
        recorder.Stop();
        string first = recorder.FilePath!;

        recorder.Start(_folder, "Programa"); // el mateix segon: ha de fer un nom diferent
        _input!.PushTone(0.5);
        recorder.Stop();

        Assert.NotEqual(first, recorder.FilePath);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(recorder.FilePath));
    }

    [Fact]
    public void Ordres_no_permeses()
    {
        using var recorder = new Recorder("1", _ => new FakeInput());

        Assert.Throws<RecorderCommandException>(() => recorder.Start(_folder));  // sense entrada
        Assert.Throws<RecorderCommandException>(() => recorder.Stop());          // no enregistra

        recorder.SetDevice(DeviceId);
        recorder.Start(_folder);
        Assert.Throws<RecorderCommandException>(() => recorder.Start(_folder));  // ja enregistra
        Assert.Throws<RecorderCommandException>(() => recorder.SetDevice("altre"));
        recorder.Stop();
    }

    [Fact]
    public void Entrada_que_no_es_pot_obrir_deixa_error_sense_fitxers()
    {
        using var recorder = new Recorder("1", _ => throw new AudioDeviceException("Entrada no disponible: no es troba a Windows."));
        recorder.SetDevice(DeviceId);

        recorder.Start(_folder);

        Assert.Equal(RecorderState.Error, recorder.State);
        Assert.Contains("no es troba", recorder.ErrorMessage);
        Assert.False(Directory.Exists(_folder) && Directory.EnumerateFiles(_folder).Any());
    }

    [Fact]
    public void Error_inesperat_en_comencar_ho_desfa_tot()
    {
        FakeInput? input = null;
        using var recorder = new Recorder("1", _ => input = new FakeInput { ThrowOnStart = true });
        recorder.SetDevice(DeviceId);

        recorder.Start(_folder, "Falla");

        Assert.Equal(RecorderState.Error, recorder.State);
        Assert.Equal("No s'ha pogut començar a enregistrar.", recorder.ErrorMessage);
        Assert.True(input!.Disposed);
        Assert.Empty(Directory.EnumerateFiles(_folder)); // ni .mp3 ni .rec

        // L'enregistrador es pot tornar a fer servir.
        Assert.Throws<RecorderCommandException>(() => recorder.Stop());
    }

    [Fact]
    public void Noms_amb_caracters_no_permesos_es_netegen()
    {
        using var recorder = NewRecorder();
        recorder.Start(_folder, "Línia 2: «entrevista»?");
        recorder.Stop();

        Assert.StartsWith("Línia 2_ «entrevista»__", Path.GetFileName(recorder.FilePath));
    }
}
