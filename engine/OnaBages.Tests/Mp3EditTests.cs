using OnaBages.Audio;

namespace OnaBages.Tests;

/// <summary>
/// Editor sense pèrdua (fase 3, pas 1; especificació 9.3): índex de fotogrames, eliminar, retallar,
/// desfer i refer, i desar copiant els fotogrames tal qual.
/// </summary>
public class Mp3EditTests : IDisposable
{
    private const int Rate = 48000;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "edit-" + Guid.NewGuid().ToString("N"));

    public Mp3EditTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>MP3 de prova (to de 440 Hz, 320 kbps CBR com els enregistraments).</summary>
    private static byte[] EncodeMp3(double seconds)
    {
        string wav = TestAudio.CreateWav(Rate, 2, seconds);
        using var source = AudioFileSource.Open(wav, Rate);
        using var encoder = new LameEncoder(Rate, 320);
        using var mp3 = new MemoryStream();
        var buffer = new float[Rate];
        int read;
        while ((read = source.Read(buffer)) > 0)
            mp3.Write(encoder.Encode(buffer, read));
        mp3.Write(encoder.Flush());
        source.Dispose();
        File.Delete(wav);
        return mp3.ToArray();
    }

    private string Write(byte[] data, string name = "prova.mp3")
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>Els bytes de cada fotograma, segons l'índex.</summary>
    private static List<byte[]> Frames(Mp3FrameIndex index, byte[] data) =>
        Enumerable.Range(0, index.FrameCount)
            .Select(i => data.AsSpan((int)index.Offset(i), index.Length(i)).ToArray())
            .ToList();

    // --- Índex ---

    [Fact]
    public void L_index_troba_tots_els_fotogrames()
    {
        byte[] data = EncodeMp3(2);
        var index = Mp3FrameIndex.Build(Write(data));

        Assert.Equal(Rate, index.SampleRate);
        Assert.Equal(1152, index.SamplesPerFrame);
        Assert.Equal([320], index.Bitrates);
        Assert.Equal(0.024, index.FrameSeconds, 6);
        Assert.InRange(index.Duration.TotalSeconds, 2.0, 2.1);
        // Els fotogrames són seguits i arriben fins al final.
        Assert.Equal(0, index.Offset(0));
        Assert.Equal(data.Length, index.Offset(index.FrameCount - 1) + index.Length(index.FrameCount - 1));
    }

    [Fact]
    public void L_index_salta_ID3_i_el_fotograma_Info_i_ho_guarda_a_part()
    {
        byte[] mp3 = EncodeMp3(1);
        // Fotograma Info fals: una còpia del primer amb «Info» on el posa LAME (després de 32 bytes d'informació lateral).
        int firstLength = Mp3FrameIndex.Build(Write(mp3)).Length(0);
        byte[] info = mp3[..firstLength];
        "Info"u8.CopyTo(info.AsSpan(36));
        byte[] id3v2 = [(byte)'I', (byte)'D', (byte)'3', 3, 0, 0, 0, 0, 0, 5, 1, 2, 3, 4, 5];
        byte[] id3v1 = new byte[128];
        "TAG"u8.CopyTo(id3v1);
        byte[] data = [.. id3v2, .. info, .. mp3, .. id3v1];

        var index = Mp3FrameIndex.Build(Write(data));
        var plain = Mp3FrameIndex.Build(Write(mp3, "sol.mp3"));

        Assert.Equal(15, index.Id3v2Length);
        Assert.Equal(data.Length - 128, index.Id3v1Offset);
        Assert.Equal(plain.FrameCount, index.FrameCount); // el fotograma Info no hi és
        Assert.Equal(15 + firstLength, index.Offset(0));
    }

    [Fact]
    public void Bytes_que_no_son_fotogrames_es_salten()
    {
        byte[] mp3 = EncodeMp3(1);
        var plain = Mp3FrameIndex.Build(Write(mp3, "sol.mp3"));
        int cut = (int)plain.Offset(10);
        byte[] data = [.. mp3[..cut], 1, 2, 3, 4, 5, 6, 7, .. mp3[cut..]];

        var index = Mp3FrameIndex.Build(Write(data));

        Assert.Equal(plain.FrameCount, index.FrameCount);
        Assert.Equal(cut + 7, index.Offset(10));
    }

    [Fact]
    public void Un_fitxer_que_no_es_mp3_dona_un_error_clar()
    {
        string path = Write([.. Enumerable.Range(0, 5000).Select(i => (byte)(i * 7))], "soroll.mp3");

        var ex = Assert.Throws<Mp3EditException>(() => Mp3FrameIndex.Build(path));
        Assert.Contains("no és un MP3", ex.Message);
    }

    // --- Edició ---

    private Mp3Edit Open(double seconds, out byte[] data)
    {
        data = EncodeMp3(seconds);
        return new Mp3Edit(Mp3FrameIndex.Build(Write(data)));
    }

    [Fact]
    public void Eliminar_un_fragment_del_mig()
    {
        var edit = Open(2, out _);
        int total = edit.FrameCount;

        Assert.True(edit.Delete(10, 20));

        Assert.Equal(total - 10, edit.FrameCount);
        Assert.Equal([new FrameRange(0, 10), new FrameRange(20, total - 20)], edit.Segments);
        Assert.True(edit.IsModified);
        Assert.Equal(20, edit.ToOriginal(10)); // just després del tall
    }

    [Fact]
    public void Retallar_inici_i_final()
    {
        var edit = Open(2, out _);
        int total = edit.FrameCount;

        edit.Delete(0, 5);            // retallar l'inici
        edit.Delete(edit.FrameCount - 5, edit.FrameCount); // retallar el final

        Assert.Equal([new FrameRange(5, total - 10)], edit.Segments);
    }

    [Fact]
    public void Les_posicions_son_de_la_versio_editada()
    {
        var edit = Open(2, out _);
        edit.Delete(10, 20);   // ara el fotograma 10 de l'editada és el 20 de l'original
        edit.Delete(10, 15);   // elimina els originals 20..24

        Assert.Equal([new FrameRange(0, 10), new FrameRange(25, edit.Index.FrameCount - 25)], edit.Segments);
    }

    [Fact]
    public void Conservar_nomes_la_seleccio_es_un_sol_pas()
    {
        var edit = Open(2, out _);
        edit.Delete(10, 20);

        edit.Keep(5, 15);      // de l'editada: originals 5..9 i 20..24

        Assert.Equal([new FrameRange(5, 5), new FrameRange(20, 5)], edit.Segments);
        edit.Undo();
        Assert.Equal(2, edit.Segments.Count); // torna a l'estat d'abans del Keep, no a mig camí
        Assert.Equal(edit.Index.FrameCount - 10, edit.FrameCount);
    }

    [Fact]
    public void Desfer_i_refer()
    {
        var edit = Open(2, out _);
        int total = edit.FrameCount;
        edit.Delete(0, 10);
        edit.Delete(0, 10);

        Assert.True(edit.Undo());
        Assert.Equal(total - 10, edit.FrameCount);
        Assert.True(edit.Undo());
        Assert.False(edit.IsModified);
        Assert.False(edit.Undo());

        Assert.True(edit.Redo());
        Assert.Equal(total - 10, edit.FrameCount);

        edit.Delete(0, 1);     // un canvi nou esborra el que es podia refer
        Assert.False(edit.CanRedo);
    }

    [Fact]
    public void No_es_pot_eliminar_tot()
    {
        var edit = Open(1, out _);

        Assert.Throws<Mp3EditException>(() => edit.Delete(0, edit.FrameCount));
        Assert.False(edit.Delete(5, 5)); // selecció buida: res
        Assert.False(edit.IsModified);
    }

    [Fact]
    public void Una_seleccio_al_reves_o_fora_de_marge_s_ajusta()
    {
        var edit = Open(1, out _);
        int total = edit.FrameCount;

        edit.Delete(total + 50, total - 5); // es converteix en [total-5, total)

        Assert.Equal(total - 5, edit.FrameCount);
    }

    [Fact]
    public void Temps_i_fotogrames()
    {
        var edit = Open(2, out _);

        Assert.Equal(42, edit.FrameAt(1.0));      // 1 s / 24 ms = 41,67 → 42
        Assert.Equal(0, edit.FrameAt(-3));
        Assert.Equal(edit.FrameCount, edit.FrameAt(999));
        Assert.Equal(0.024 * 42, edit.SecondsAt(42), 6);
    }

    // --- Desar ---

    [Fact]
    public void Desar_copia_els_fotogrames_tal_qual()
    {
        var edit = Open(3, out byte[] original);
        var originalFrames = Frames(edit.Index, original);
        edit.Delete(20, 50);
        edit.Delete(0, 3);
        string target = Path.Combine(_folder, "editat.mp3");

        edit.Save(target);

        byte[] saved = File.ReadAllBytes(target);
        var index = Mp3FrameIndex.Build(target);
        var expected = originalFrames.Skip(3).Take(17).Concat(originalFrames.Skip(50)).ToList();
        Assert.Equal(expected.Count, index.FrameCount);
        Assert.Equal(expected.SelectMany(f => f).ToArray(), saved); // byte a byte: cap recodificació
    }

    [Fact]
    public void Desar_conserva_les_etiquetes_ID3_i_treu_el_fotograma_Info()
    {
        byte[] mp3 = EncodeMp3(1);
        int firstLength = Mp3FrameIndex.Build(Write(mp3, "sol.mp3")).Length(0);
        byte[] info = mp3[..firstLength];
        "Info"u8.CopyTo(info.AsSpan(36));
        byte[] id3v2 = [(byte)'I', (byte)'D', (byte)'3', 3, 0, 0, 0, 0, 0, 5, 1, 2, 3, 4, 5];
        byte[] id3v1 = new byte[128];
        "TAG"u8.CopyTo(id3v1);
        var edit = new Mp3Edit(Mp3FrameIndex.Build(Write([.. id3v2, .. info, .. mp3, .. id3v1])));
        string target = Path.Combine(_folder, "editat.mp3");

        edit.Save(target); // sense canvis: igual que l'original menys el fotograma Info

        Assert.Equal([.. id3v2, .. mp3, .. id3v1], File.ReadAllBytes(target));
    }

    [Fact]
    public void El_fitxer_desat_es_pot_reproduir()
    {
        var edit = Open(3, out _);
        edit.Delete(25, 75);   // treu 1,2 s
        string target = Path.Combine(_folder, "editat.mp3");
        edit.Save(target);

        using var source = AudioFileSource.Open(target, Rate);
        double seconds = TestAudio.ReadAll(source).Length / 2.0 / Rate;

        Assert.InRange(seconds, 1.7, 1.95);
    }

    [Fact]
    public void Sobreescriure_l_original()
    {
        var edit = Open(2, out _);
        int total = edit.FrameCount;
        edit.Delete(0, 10);

        edit.Save(edit.Index.Path);

        Assert.Equal(total - 10, Mp3FrameIndex.Build(edit.Index.Path).FrameCount);
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp")); // no queda cap temporal
    }

    [Fact]
    public void Si_l_original_ha_canviat_no_es_desa()
    {
        var edit = Open(1, out _);
        File.AppendAllText(edit.Index.Path, "x");
        string target = Path.Combine(_folder, "editat.mp3");

        var ex = Assert.Throws<Mp3EditException>(() => edit.Save(target));

        Assert.Contains("ha canviat", ex.Message);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void Si_no_es_pot_desar_la_destinacio_queda_intacta()
    {
        var edit = Open(1, out _);
        edit.Delete(0, 5);
        string target = Path.Combine(_folder, "ocupat.mp3");
        File.WriteAllText(target, "abans");

        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read)) // obert per un altre programa
            Assert.Throws<Mp3EditException>(() => edit.Save(target));

        Assert.Equal("abans", File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public void Desar_informa_del_progres()
    {
        var edit = Open(2, out _);
        var progress = new List<double>();

        edit.Save(Path.Combine(_folder, "editat.mp3"), progress.Add);

        Assert.NotEmpty(progress);
        Assert.Equal(1.0, progress[^1], 6);
    }
}
