using OnaBages.Audio;

namespace OnaBages.Tests;

/// <summary>
/// Volum dels fotogrames MP3 sense tornar a codificar (fase 4, pas 6a; D-073). Es codifica un to amb LAME
/// en diversos formats (MPEG-1 i MPEG-2, estèreo i mono, amb CRC), es canvia el volum global dels
/// fotogrames i es descodifica per mesurar-ne el resultat.
/// </summary>
public class Mp3GainTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "mp3gain-" + Guid.NewGuid().ToString("N"));

    public Mp3GainTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>MP3 amb 3 s de to de 1 kHz a −12 dBFS.</summary>
    private string Encode(int rate, int kbps, bool mono = false, bool crc = false)
    {
        string path = Path.Combine(_folder, $"to-{rate}-{kbps}-{(mono ? "mono" : "st")}-{(crc ? "crc" : "")}.mp3");
        using var encoder = new LameEncoder(rate, kbps, mono, crc);
        using var file = File.Create(path);
        var block = new float[rate / 10 * 2];
        float amplitude = (float)Math.Pow(10, -12 / 20.0);
        for (int n = 0; n < 30; n++)
        {
            for (int f = 0; f < block.Length / 2; f++)
                block[f * 2] = block[f * 2 + 1] = amplitude * (float)Math.Sin(2 * Math.PI * 1000 * (n * block.Length / 2 + f) / rate);
            file.Write(encoder.Encode(block, block.Length));
        }
        file.Write(encoder.Flush());
        return path;
    }

    /// <summary>Canvia el volum de tots els grànuls de tots els fotogrames i en desa una còpia.</summary>
    private string Adjust(string path, Func<int, int, int> steps)
    {
        var index = Mp3FrameIndex.Build(path);
        byte[] bytes = File.ReadAllBytes(path);
        for (int f = 0; f < index.FrameCount; f++)
        {
            var frame = bytes.AsSpan((int)index.Offset(f), index.Length(f));
            for (int g = 0; g < Mp3Gain.GranuleCount(frame); g++)
                Assert.True(Mp3Gain.Adjust(frame, g, steps(f, g)));
        }
        string copy = Path.Combine(_folder, Path.GetFileNameWithoutExtension(path) + "-volum.mp3");
        File.WriteAllBytes(copy, bytes);
        return copy;
    }

    /// <summary>Nivell RMS (dB) del segon del mig.</summary>
    private static double MiddleRmsDb(string path)
    {
        using var source = AudioFileSource.Open(path, 48000);
        var all = new float[48000 * 2 * 4];
        int total = 0, read;
        while ((read = source.Read(all.AsSpan(total))) > 0 && total < all.Length) total += read;
        double sum = 0;
        int start = 48000 * 2, count = 48000 * 2;
        for (int i = start; i < start + count; i++) sum += all[i] * all[i];
        return 10 * Math.Log10(sum / count);
    }

    public static TheoryData<int, int, bool, bool> Formats => new()
    {
        { 48000, 320, false, false }, // MPEG-1 estèreo (com els enregistraments)
        { 44100, 128, true, false },  // MPEG-1 mono
        { 22050, 64, false, false },  // MPEG-2 estèreo (un sol grànul per fotograma)
        { 22050, 32, true, false },   // MPEG-2 mono
        { 44100, 192, false, true },  // amb CRC
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void Quatre_passos_menys_son_6_dB_menys(int rate, int kbps, bool mono, bool crc)
    {
        string original = Encode(rate, kbps, mono, crc);

        string quieter = Adjust(original, (_, _) => -4);

        Assert.Equal(new FileInfo(original).Length, new FileInfo(quieter).Length); // la mida no canvia
        Assert.InRange(MiddleRmsDb(quieter) - MiddleRmsDb(original), -6.15, -5.85);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void El_volum_es_llegeix_i_canvia_a_tots_els_canals(int rate, int kbps, bool mono, bool crc)
    {
        string original = Encode(rate, kbps, mono, crc);
        var index = Mp3FrameIndex.Build(original);
        byte[] bytes = File.ReadAllBytes(original);
        var frame = bytes.AsSpan((int)index.Offset(10), index.Length(10));
        int channels = Mp3Gain.ChannelCount(frame);
        Assert.Equal(mono ? 1 : 2, channels);
        Assert.Equal(rate > 24000 ? 2 : 1, Mp3Gain.GranuleCount(frame));

        int before = Mp3Gain.Read(frame, 0, channels - 1);
        Mp3Gain.Adjust(frame, 0, +3);

        Assert.InRange(before, 100, 250);
        Assert.Equal(before + 3, Mp3Gain.Read(frame, 0, channels - 1));
    }

    [Fact]
    public void Amb_CRC_es_torna_a_calcular()
    {
        string original = Encode(44100, 192, crc: true);
        var index = Mp3FrameIndex.Build(original);
        byte[] bytes = File.ReadAllBytes(original);
        var frame = bytes.AsSpan((int)index.Offset(5), index.Length(5));
        Assert.True(Mp3Gain.CrcIsValid(frame)); // el que calcula LAME coincideix amb el nostre

        Mp3Gain.Adjust(frame, 1, -7);

        Assert.True(Mp3Gain.CrcIsValid(frame));
    }

    [Fact]
    public void El_CRC_es_el_de_l_MPEG()
    {
        // Valor de comprovació del CRC-16 amb polinomi 0x8005 i inici 0xFFFF (CRC-16/CMS).
        Assert.Equal(0xAEE7, Mp3Gain.Crc16("123456789"u8));
    }

    [Fact]
    public void A_zero_es_silenci()
    {
        string original = Encode(48000, 320);

        string silent = Adjust(original, (_, _) => int.MinValue);

        Assert.True(MiddleRmsDb(silent) < -90);
    }

    [Fact]
    public void Un_fos_per_granuls_baixa_de_mica_en_mica()
    {
        string original = Encode(48000, 320);
        // Del fotograma 40 en endavant, 1 pas menys per grànul (fos de sortida molt ràpid).
        string faded = Adjust(original, (f, g) => f < 40 ? 0 : -((f - 40) * 2 + g + 1));

        double level = MiddleRmsDb(original);
        double fadedLevel = MiddleRmsDb(faded);

        Assert.True(fadedLevel < level - 20, $"{fadedLevel:0.0} dB, havia de ser molt més fluix que {level:0.0}");
    }

    [Fact]
    public void Un_fitxer_que_no_es_MP3_no_es_toca()
    {
        byte[] noise = [0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0];

        Assert.False(Mp3Gain.Adjust(noise, 0, -4));
        Assert.Equal(-1, Mp3Gain.Read(noise, 0, 0));
        Assert.Equal([0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0], noise);
    }
}
