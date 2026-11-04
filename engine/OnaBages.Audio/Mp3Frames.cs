namespace OnaBages.Audio;

/// <summary>Resultat d'analitzar un fitxer MP3 fotograma a fotograma.</summary>
/// <param name="Frames">Fotogrames complets.</param>
/// <param name="SampleRate">Freqüència del primer fotograma (0 si no n'hi ha cap).</param>
/// <param name="Bitrates">Bitrates diferents trobats, en kbps (en CBR, només un).</param>
/// <param name="HasXingTag">El primer fotograma és una etiqueta Xing/Info (no hauria de passar).</param>
/// <param name="ValidLength">Bytes des de l'inici fins al final de l'últim fotograma complet.</param>
/// <param name="TrailingBytes">Bytes de després de l'últim fotograma complet (un fotograma tallat).</param>
/// <param name="Duration">Durada segons els fotogrames complets.</param>
/// <param name="FirstFrameOffset">On comença el primer fotograma (després d'una etiqueta ID3v2, si n'hi ha).</param>
/// <param name="FirstFrameLength">Bytes del primer fotograma (0 si no n'hi ha cap).</param>
public sealed record Mp3Analysis(
    int Frames,
    int SampleRate,
    IReadOnlyList<int> Bitrates,
    bool HasXingTag,
    long ValidLength,
    long TrailingBytes,
    TimeSpan Duration,
    long FirstFrameOffset = 0,
    int FirstFrameLength = 0);

/// <summary>
/// Lectura de les capçaleres dels fotogrames MP3 (MPEG-1 i MPEG-2 capa III), sense descodificar.
/// Serveix per comprovar els enregistraments i, més endavant, per recuperar-los
/// (retallar fins a l'últim fotograma complet) i per a l'editor.
/// </summary>
public static class Mp3Frames
{
    private static readonly int[] Mpeg1Bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
    private static readonly int[] Mpeg2Bitrates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];
    private static readonly int[] Mpeg1Rates = [44100, 48000, 32000, 0];

    /// <summary>
    /// Analitza un fitxer llegint-lo per trossos (no el carrega sencer a la memòria).
    /// El pot llegir encara que un altre procés hi estigui escrivint.
    /// </summary>
    public static Mp3Analysis Analyze(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024);
        return Analyze(stream);
    }

    public static Mp3Analysis Analyze(byte[] data) => Analyze(new MemoryStream(data, writable: false));

    public static Mp3Analysis Analyze(Stream stream)
    {
        int frames = 0, sampleRate = 0, samplesPerFrame = 1152;
        int firstFrameLength = 0;
        var bitrates = new SortedSet<int>();
        bool xing = false;
        long length = stream.Length;
        var head = new byte[10];

        long pos = SkipId3v2(stream, head);
        long firstFrameOffset = pos;
        long validEnd = pos;

        while (pos + 4 <= length)
        {
            stream.Position = pos;
            stream.ReadExactly(head, 0, 4);
            if (!TryParseHeader(head, out var header))
            {
                // No és un fotograma: brossa o un fotograma tallat. No seguim buscant
                // (en els nostres fitxers els fotogrames són consecutius).
                break;
            }

            if (pos + header.Length > length)
                break; // l'últim fotograma no és complet

            if (frames == 0)
            {
                sampleRate = header.SampleRate;
                samplesPerFrame = header.SamplesPerFrame;
                firstFrameLength = header.Length;
                var first = new byte[header.Length];
                stream.Position = pos;
                stream.ReadExactly(first);
                xing = ContainsXingTag(first);
            }

            frames++;
            bitrates.Add(header.BitrateKbps);
            pos += header.Length;
            validEnd = pos;
        }

        var duration = sampleRate > 0
            ? TimeSpan.FromSeconds((double)frames * samplesPerFrame / sampleRate)
            : TimeSpan.Zero;

        return new Mp3Analysis(frames, sampleRate, bitrates.ToList(), xing, validEnd, length - validEnd, duration,
            firstFrameOffset, firstFrameLength);
    }
    internal readonly record struct FrameHeader(int BitrateKbps, int SampleRate, int Length, int SamplesPerFrame);

    /// <summary>Capçalera de 4 bytes d'un fotograma de capa III.</summary>
    internal static bool TryParseHeader(ReadOnlySpan<byte> b, out FrameHeader header)
    {
        header = default;
        if (b.Length < 4 || b[0] != 0xFF || (b[1] & 0xE0) != 0xE0) return false;

        int version = (b[1] >> 3) & 0x03;   // 3 = MPEG-1, 2 = MPEG-2, 0 = MPEG-2.5
        int layer = (b[1] >> 1) & 0x03;     // 1 = capa III
        int bitrateIndex = (b[2] >> 4) & 0x0F;
        int rateIndex = (b[2] >> 2) & 0x03;
        int padding = (b[2] >> 1) & 0x01;
        if (version == 1 || layer != 1 || bitrateIndex is 0 or 15 || rateIndex == 3) return false;

        bool mpeg1 = version == 3;
        int bitrate = (mpeg1 ? Mpeg1Bitrates : Mpeg2Bitrates)[bitrateIndex];
        int sampleRate = Mpeg1Rates[rateIndex] / (mpeg1 ? 1 : version == 2 ? 2 : 4);
        int samplesPerFrame = mpeg1 ? 1152 : 576;
        int length = samplesPerFrame / 8 * bitrate * 1000 / sampleRate + padding;

        header = new FrameHeader(bitrate, sampleRate, length, samplesPerFrame);
        return true;
    }

    /// <summary>LAME escriu «Xing» (VBR) o «Info» (CBR) dins del primer fotograma.</summary>
    internal static bool ContainsXingTag(ReadOnlySpan<byte> frame) =>
        frame.IndexOf("Xing"u8) >= 0 || frame.IndexOf("Info"u8) >= 0;

    /// <summary>Salta una etiqueta ID3v2 inicial, si n'hi ha.</summary>
    internal static long SkipId3v2(Stream stream, byte[] head)
    {
        if (stream.Length < 10) return 0;
        stream.Position = 0;
        stream.ReadExactly(head, 0, 10);
        var data = head;
        if (data[0] != 'I' || data[1] != 'D' || data[2] != '3') return 0;
        int size = (data[6] & 0x7F) << 21 | (data[7] & 0x7F) << 14 | (data[8] & 0x7F) << 7 | (data[9] & 0x7F);
        return 10 + size;
    }
}
