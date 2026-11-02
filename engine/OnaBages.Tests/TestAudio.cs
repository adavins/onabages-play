using NAudio.Wave;
using OnaBages.Audio;

namespace OnaBages.Tests;

/// <summary>Eines per a les proves d'àudio: fitxers de prova i una sortida falsa.</summary>
internal static class TestAudio
{
    /// <summary>
    /// Crea un WAV de prova en una carpeta temporal. Cada canal porta un valor diferent
    /// (0,1 · número de canal) multiplicat per una ona sinusoïdal, per poder-los distingir.
    /// </summary>
    public static string CreateWav(int sampleRate, int channels, double seconds, bool floatFormat = true)
    {
        string dir = Path.Combine(Path.GetTempPath(), "OnaBagesTests");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"prova-{Guid.NewGuid():N}.wav");

        var format = floatFormat
            ? WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels)
            : new WaveFormat(sampleRate, 16, channels);

        int frames = (int)(sampleRate * seconds);
        var samples = new float[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            float sine = (float)Math.Sin(2 * Math.PI * 440 * i / sampleRate);
            for (int c = 0; c < channels; c++)
                samples[i * channels + c] = 0.1f * (c + 1) * sine;
        }

        using (var writer = new WaveFileWriter(path, format))
            writer.WriteSamples(samples, 0, samples.Length);

        return path;
    }

    /// <summary>Llegeix tot un AudioFileSource i en retorna les mostres.</summary>
    public static float[] ReadAll(AudioFileSource source)
    {
        var all = new List<float>();
        var buffer = new float[4096];
        int read;
        while ((read = source.Read(buffer)) > 0)
            all.AddRange(buffer.AsSpan(0, read).ToArray());
        return all.ToArray();
    }
}

/// <summary>
/// Entrada falsa: <see cref="Push"/> fa el paper del fil de captura de Windows.
/// </summary>
internal sealed class FakeInput(int sampleRate = 48000, int channels = 2) : IAudioInput
{
    public int SampleRate => sampleRate;
    public int Channels => channels;
    public bool IsRunning { get; private set; }
    public bool Disposed { get; private set; }
    public event SamplesCapturedHandler? DataAvailable;
    public event Action<Exception?>? Stopped;

    /// <summary>Si és cert, Start() falla amb un error inesperat (per provar la neteja).</summary>
    public bool ThrowOnStart { get; init; }

    public void Start()
    {
        if (ThrowOnStart) throw new InvalidOperationException("error inesperat del controlador");
        IsRunning = true;
    }

    public void Stop() => IsRunning = false;
    public void Dispose() => Disposed = true;

    /// <summary>Envia mostres com si arribessin de la targeta.</summary>
    public void Push(float[] samples, bool silent = false, bool discontinuity = false) =>
        DataAvailable?.Invoke(samples, silent, discontinuity);

    /// <summary>Envia <paramref name="seconds"/> d'un to de 440 Hz, en paquets de 10 ms.</summary>
    public void PushTone(double seconds)
    {
        int packet = sampleRate / 100;
        int total = (int)(sampleRate * seconds);
        var buffer = new float[packet * channels];
        for (int start = 0; start < total; start += packet)
        {
            for (int i = 0; i < packet; i++)
            {
                float v = 0.3f * (float)Math.Sin(2 * Math.PI * 440 * (start + i) / sampleRate);
                for (int c = 0; c < channels; c++) buffer[i * channels + c] = v;
            }
            Push(buffer);
        }
    }

    /// <summary>Simula que l'entrada s'ha desconnectat.</summary>
    public void Fail(string message) => Stopped?.Invoke(new IOException(message));
}

/// <summary>
/// Sortida falsa: no fa so. <see cref="Pump"/> fa el paper del fil d'àudio de Windows
/// i demana mostres a la font.
/// </summary>
internal sealed class FakeOutput(int sampleRate = 48000, int channels = 2) : IAudioOutput
{
    private IWaveProvider? _source;

    public int SampleRate => sampleRate;
    public int Channels => channels;
    public bool IsPlaying { get; private set; }
    public bool Disposed { get; private set; }
    public event Action<Exception?>? Stopped;

    public void Init(IWaveProvider source) => _source = source;
    public void Play() => IsPlaying = true;
    public void Pause() => IsPlaying = false;
    public void Stop() => IsPlaying = false;
    public void Dispose() => Disposed = true;

    /// <summary>Demana <paramref name="frames"/> mostres per canal, com faria Windows.</summary>
    public float[] Pump(int frames)
    {
        var bytes = new byte[frames * channels * sizeof(float)];
        _source!.Read(bytes);
        var samples = new float[frames * channels];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return samples;
    }

    /// <summary>Simula que el dispositiu s'ha desconnectat.</summary>
    public void Fail(string message) => Stopped?.Invoke(new IOException(message));
}
