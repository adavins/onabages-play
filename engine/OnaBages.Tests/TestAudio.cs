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
