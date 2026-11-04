namespace OnaBages.Audio;

/// <summary>Error en obrir o desar un fitxer a l'editor. El missatge és per a l'operador.</summary>
public sealed class Mp3EditException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Índex dels fotogrames d'un MP3: on comença cada un i quant ocupa. És la base de l'editor
/// (especificació 9.3): retallar = copiar fotogrames sencers, sense descodificar ni tornar a codificar.
///
/// - L'etiqueta ID3v2 del principi i l'ID3v1 del final (si n'hi ha) es guarden a part i es copien tal qual.
/// - El fotograma Xing/Info de LAME (si n'hi ha) no entra a l'índex: diu la durada i la mida del
///   fitxer original, que després d'editar ja no serien certes. Sense ell, el fitxer és igual de vàlid.
/// - Si enmig hi ha bytes que no són un fotograma (fitxers d'altres programes), se'ls salta i
///   continua al següent fotograma vàlid.
/// </summary>
public sealed class Mp3FrameIndex
{
    /// <summary>Fins on es busca el fotograma següent si hi ha bytes que no ho són.</summary>
    private const int MaxResync = 64 * 1024;

    private readonly long[] _offsets;
    private readonly int[] _lengths;

    private Mp3FrameIndex(string path, long fileSize, DateTime lastWrite, long[] offsets, int[] lengths,
        int sampleRate, int samplesPerFrame, IReadOnlyList<int> bitrates, long id3v2Length, long id3v1Offset)
    {
        Path = path;
        FileSize = fileSize;
        LastWriteUtc = lastWrite;
        _offsets = offsets;
        _lengths = lengths;
        SampleRate = sampleRate;
        SamplesPerFrame = samplesPerFrame;
        Bitrates = bitrates;
        Id3v2Length = id3v2Length;
        Id3v1Offset = id3v1Offset;
    }

    public string Path { get; }

    /// <summary>Mida i data del fitxer quan es va indexar (per saber si ha canviat abans de desar).</summary>
    public long FileSize { get; }
    public DateTime LastWriteUtc { get; }

    public int FrameCount => _offsets.Length;
    public int SampleRate { get; }
    public int SamplesPerFrame { get; }

    /// <summary>Bitrates trobats, en kbps (un de sol si és CBR, com els enregistraments).</summary>
    public IReadOnlyList<int> Bitrates { get; }

    /// <summary>Bytes de l'etiqueta ID3v2 del principi (0 si no n'hi ha).</summary>
    public long Id3v2Length { get; }

    /// <summary>On comença l'etiqueta ID3v1 del final (-1 si no n'hi ha).</summary>
    public long Id3v1Offset { get; }

    /// <summary>Durada d'un fotograma (≈ 24 ms a 48 kHz, ≈ 26 ms a 44,1 kHz).</summary>
    public double FrameSeconds => (double)SamplesPerFrame / SampleRate;

    public TimeSpan Duration => TimeSpan.FromSeconds(FrameCount * FrameSeconds);

    public long Offset(int frame) => _offsets[frame];
    public int Length(int frame) => _lengths[frame];

    /// <summary>
    /// Indexa un fitxer. El llegeix per trossos (no el carrega sencer) i en tanca l'accés en acabar.
    /// </summary>
    /// <param name="cancel">Per aturar-ho (p. ex. si es tanca l'editor mentre s'obre un fitxer molt llarg).</param>
    /// <exception cref="Mp3EditException">Si no existeix, no es pot llegir o no és un MP3.</exception>
    public static Mp3FrameIndex Build(string path, CancellationToken cancel = default)
    {
        if (!File.Exists(path))
            throw new Mp3EditException($"No es troba el fitxer: {path}");

        try
        {
            var info = new FileInfo(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 256 * 1024);
            return Build(stream, path, info.Length, info.LastWriteTimeUtc, cancel);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new Mp3EditException($"No s'ha pogut llegir el fitxer {System.IO.Path.GetFileName(path)}: {ex.Message}", ex);
        }
    }

    /// <summary>Indexa des d'un flux (les proves en fan servir un a memòria).</summary>
    public static Mp3FrameIndex Build(Stream stream, string path, long fileSize, DateTime lastWrite, CancellationToken cancel = default)
    {
        long length = stream.Length;
        var head = new byte[10];
        long id3v2 = Mp3Frames.SkipId3v2(stream, head);

        // ID3v1: 128 bytes al final que comencen per «TAG».
        long end = length;
        long id3v1 = -1;
        if (length - id3v2 >= 128)
        {
            stream.Position = length - 128;
            stream.ReadExactly(head, 0, 3);
            if (head[0] == 'T' && head[1] == 'A' && head[2] == 'G')
            {
                id3v1 = length - 128;
                end = id3v1;
            }
        }

        var offsets = new List<long>();
        var lengths = new List<int>();
        var bitrates = new SortedSet<int>();
        int sampleRate = 0, samplesPerFrame = 0;
        var frameStart = new byte[40];
        long pos = id3v2;

        while (pos + 4 <= end)
        {
            if ((offsets.Count & 0xFFF) == 0) cancel.ThrowIfCancellationRequested();

            stream.Position = pos;
            stream.ReadExactly(head, 0, 4);
            if (!Mp3Frames.TryParseHeader(head, out var header) || pos + header.Length > end
                || (sampleRate != 0 && header.SampleRate != sampleRate))
            {
                // No és un fotograma (o és d'un altre format): buscar el següent.
                long next = Resync(stream, pos + 1, end, sampleRate);
                if (next < 0) break;
                pos = next;
                continue;
            }

            if (offsets.Count == 0 && lengths.Count == 0 && IsXingFrame(stream, pos, header, frameStart))
            {
                // Fotograma d'informació de LAME: no és àudio i després d'editar seria fals.
                pos += header.Length;
                sampleRate = header.SampleRate;
                samplesPerFrame = header.SamplesPerFrame;
                continue;
            }

            sampleRate = header.SampleRate;
            samplesPerFrame = header.SamplesPerFrame;
            offsets.Add(pos);
            lengths.Add(header.Length);
            bitrates.Add(header.BitrateKbps);
            pos += header.Length;
        }

        if (offsets.Count == 0)
            throw new Mp3EditException($"El fitxer {System.IO.Path.GetFileName(path)} no és un MP3 (o no té àudio).");

        return new Mp3FrameIndex(path, fileSize, lastWrite, offsets.ToArray(), lengths.ToArray(),
            sampleRate, samplesPerFrame, bitrates.ToList(), id3v2, id3v1);
    }

    /// <summary>
    /// Busca el fotograma següent a partir de <paramref name="from"/>: una capçalera vàlida seguida
    /// d'una altra de vàlida (així no es confon amb bytes d'àudio que s'hi assemblin).
    /// </summary>
    private static long Resync(Stream stream, long from, long end, int sampleRate)
    {
        long limit = Math.Min(end - 4, from + MaxResync);
        var buffer = new byte[4];
        for (long p = from; p <= limit; p++)
        {
            stream.Position = p;
            if (stream.ReadByte() != 0xFF) continue;
            stream.Position = p;
            stream.ReadExactly(buffer);
            if (!Mp3Frames.TryParseHeader(buffer, out var h) || (sampleRate != 0 && h.SampleRate != sampleRate)) continue;

            long next = p + h.Length;
            if (next == end) return p; // l'últim fotograma del fitxer
            if (next + 4 > end) continue;
            stream.Position = next;
            stream.ReadExactly(buffer);
            if (Mp3Frames.TryParseHeader(buffer, out var h2) && h2.SampleRate == h.SampleRate) return p;
        }
        return -1;
    }

    /// <summary>
    /// És el fotograma Xing/Info de LAME? L'etiqueta és just després de la informació lateral,
    /// que fa 32 bytes (MPEG-1 estèreo), 17 (MPEG-1 mono o MPEG-2 estèreo) o 9 (MPEG-2 mono).
    /// </summary>
    private static bool IsXingFrame(Stream stream, long pos, Mp3Frames.FrameHeader header, byte[] buffer)
    {
        int count = Math.Min(buffer.Length, header.Length);
        stream.Position = pos;
        stream.ReadExactly(buffer, 0, count);
        foreach (int at in new[] { 4 + 32, 4 + 17, 4 + 9 })
        {
            if (at + 4 > count) continue;
            var tag = buffer.AsSpan(at, 4);
            if (tag.SequenceEqual("Xing"u8) || tag.SequenceEqual("Info"u8)) return true;
        }
        return false;
    }
}
