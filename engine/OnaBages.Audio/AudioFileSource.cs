using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace OnaBages.Audio;

/// <summary>Error en obrir o llegir un fitxer d'àudio. El missatge és per a l'operador.</summary>
public sealed class AudioFileException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Un fitxer d'àudio obert per llegir-lo. Sempre en dona mostres <b>estèreo</b> (float)
/// a la freqüència demanada (la de la targeta), sigui quin sigui el format original:
/// - mono → es duplica a tots dos canals;
/// - més de 2 canals → es fan servir els dos primers;
/// - freqüència diferent → es remostreja.
///
/// La lectura (<see cref="Read"/>) accedeix al disc: no s'ha de cridar mai des del fil d'àudio.
/// </summary>
public sealed class AudioFileSource : IDisposable
{
    private readonly WaveStream _reader;
    private readonly ISampleProvider _output;

    private AudioFileSource(WaveStream reader, int targetSampleRate, TimeSpan startAt)
    {
        _reader = reader;
        Duration = reader.TotalTime;
        SourceSampleRate = reader.WaveFormat.SampleRate;
        SourceChannels = reader.WaveFormat.Channels;

        // Començar a mig fitxer (p. ex. en reintentar després d'un error). Es fa abans de
        // muntar el remostrejador perquè aquest comenci net.
        if (startAt > TimeSpan.Zero)
            reader.CurrentTime = startAt < Duration ? startAt : Duration;

        ISampleProvider samples = ToStereo(reader.ToSampleProvider());
        // 0 = la freqüència del fitxer, sense remostrejar (p. ex. per analitzar-lo amb precisió de mostra).
        if (targetSampleRate > 0 && samples.WaveFormat.SampleRate != targetSampleRate)
            samples = new WdlResamplingSampleProvider(samples, targetSampleRate);

        _output = samples;
    }

    /// <summary>Durada total del fitxer.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Freqüència original del fitxer.</summary>
    public int SourceSampleRate { get; }

    /// <summary>Canals originals del fitxer (1 = mono, 2 = estèreo...).</summary>
    public int SourceChannels { get; }

    /// <summary>Freqüència de les mostres que dona <see cref="Read"/>.</summary>
    public int SampleRate => _output.WaveFormat.SampleRate;

    /// <summary>
    /// Obre un fitxer. Els WAV es llegeixen directament; la resta de formats (MP3, AAC/M4A,
    /// FLAC, WMA...) amb Media Foundation de Windows.
    /// </summary>
    /// <param name="targetSampleRate">Freqüència de sortida (es remostreja si cal). 0 = la del fitxer.</param>
    /// <param name="startAt">Punt on comença la lectura (per defecte, l'inici).</param>
    /// <exception cref="AudioFileException">Si el fitxer no existeix o no es pot llegir.</exception>
    public static AudioFileSource Open(string path, int targetSampleRate, TimeSpan startAt = default)
    {
        if (!File.Exists(path))
            throw new AudioFileException($"No es troba el fitxer: {path}");

        WaveStream? reader = null;
        try
        {
            if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
                reader = TryOpenWave(path);

            reader ??= new MediaFoundationReader(path);
            return new AudioFileSource(reader, targetSampleRate, startAt);
        }
        catch (Exception ex) when (ex is not AudioFileException)
        {
            reader?.Dispose();
            throw new AudioFileException(
                $"No s'ha pogut obrir el fitxer {Path.GetFileName(path)}: format no compatible o fitxer malmès. " +
                "Si és un MP3 o un M4A i l'ordinador té Windows «N», cal instal·lar el Media Feature Pack.", ex);
        }
    }

    /// <summary>WAV en PCM o coma flotant; si és un WAV comprimit, retorna null (es prova amb Media Foundation).</summary>
    private static WaveStream? TryOpenWave(string path)
    {
        var wave = new WaveFileReader(path);
        if (wave.WaveFormat.Encoding is WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible)
            return wave;

        wave.Dispose();
        return null;
    }

    /// <summary>Omple <paramref name="buffer"/> amb mostres estèreo intercalades. Retorna 0 al final del fitxer.</summary>
    public int Read(Span<float> buffer) => _output.Read(buffer);

    public void Dispose() => _reader.Dispose();

    private static ISampleProvider ToStereo(ISampleProvider source) => source.WaveFormat.Channels switch
    {
        1 => new MonoToStereoSampleProvider(source),
        2 => source,
        _ => new FirstTwoChannels(source),
    };

    /// <summary>Es queda amb els dos primers canals d'un fitxer de més de 2 canals.</summary>
    private sealed class FirstTwoChannels(ISampleProvider source) : ISampleProvider
    {
        private readonly int _channels = source.WaveFormat.Channels;
        private float[] _temp = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);

        public int Read(Span<float> buffer)
        {
            int frames = buffer.Length / 2;
            int needed = frames * _channels;
            if (_temp.Length < needed) _temp = new float[needed]; // només al fil de lectura, mai al d'àudio

            int read = source.Read(_temp.AsSpan(0, needed));
            int framesRead = read / _channels;
            for (int i = 0; i < framesRead; i++)
            {
                buffer[i * 2] = _temp[i * _channels];
                buffer[i * 2 + 1] = _temp[i * _channels + 1];
            }
            return framesRead * 2;
        }
    }
}
