using NAudio.Wave;

namespace OnaBages.Audio;

/// <summary>
/// To de prova continu per detectar talls (pas 8 de la fase 1): una ona sinusoïdal
/// de <see cref="Frequency"/> Hz, igual als dos canals.
///
/// Per què 1618 Hz i no 1000: amb 1000 Hz, un tall de 10 ms exactes (un bloc típic
/// de Windows) treu 10 cicles sencers i l'ona continua sense cap salt: no es veuria.
/// 1618 Hz (proporció àuria) fa que cap tall d'un nombre de mil·lisegons enter
/// deixi l'ona en la mateixa fase.
/// </summary>
public sealed class ToneGenerator
{
    public const int Frequency = 1618;

    /// <summary>Amplitud: −12 dBFS. Prou alt per analitzar-lo, sense risc de saturar.</summary>
    public const float Amplitude = 0.25f;

    private readonly int _sampleRate;
    private long _frame;

    public ToneGenerator(int sampleRate)
    {
        _sampleRate = sampleRate;
    }

    /// <summary>Omple el bloc amb el to (estèreo intercalat), continuant on s'havia quedat.</summary>
    public void Fill(Span<float> stereo)
    {
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            // La fase es calcula exacta, amb enters, a partir del número de mostra: en hores de to no s'acumula cap error.
            long cycle = _frame * Frequency % _sampleRate;
            float value = Amplitude * (float)Math.Sin(2 * Math.PI * cycle / _sampleRate);
            stereo[i] = value;
            stereo[i + 1] = value;
            _frame++;
        }
    }

    /// <summary>
    /// Escriu un WAV de 16 bits estèreo amb el to. Ocupa uns 11 MB per minut a 48 kHz.
    /// </summary>
    public static void WriteWav(string path, TimeSpan length, int sampleRate = 48000)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var generator = new ToneGenerator(sampleRate);
        long framesLeft = (long)Math.Round(length.TotalSeconds * sampleRate);
        var buffer = new float[sampleRate * 2]; // 1 s

        using var writer = new WaveFileWriter(path, new WaveFormat(sampleRate, 16, 2));
        while (framesLeft > 0)
        {
            int frames = (int)Math.Min(framesLeft, sampleRate);
            generator.Fill(buffer.AsSpan(0, frames * 2));
            writer.WriteSamples(buffer, 0, frames * 2);
            framesLeft -= frames;
        }
    }
}
