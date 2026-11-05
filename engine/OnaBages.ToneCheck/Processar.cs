using System.Diagnostics;
using NAudio.Wave;
using OnaBages.Audio;

namespace OnaBages.ToneCheck;

/// <summary>
/// Passa un fitxer d'àudio pel compressor/limitador dels reproductors (fase 4, pas 4a; D-066) i el desa
/// en un WAV, per escoltar-lo o comparar-lo amb el que fa el plugin SqrSoft del Winamp.
/// També diu el pic d'entrada i de sortida i quant ha abaixat com a molt.
/// </summary>
public static class Processar
{
    public static int Run(string input, string output, DynamicsSettings settings)
    {
        const int rate = 48000;
        Console.WriteLine($"Entrada: {input}");
        Console.WriteLine($"Ajustos: guany {settings.InputGainDb:+0.0;-0.0} dB, llindar {settings.ThresholdDb:0.0} dB, ràtio {settings.Ratio:0.#}:1, " +
                          $"atac {settings.AttackMs:0.#} ms, alliberament {settings.ReleaseMs:0} ms, sortida {settings.OutputGainDb:+0.0;-0.0} dB, " +
                          $"canals {(settings.StereoLink ? "junts" : "separats")}, sostre {settings.CeilingDb:0.0} dB");

        AudioFileSource source;
        try
        {
            source = AudioFileSource.Open(input, rate);
        }
        catch (AudioFileException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var sw = Stopwatch.StartNew();
        var dynamics = new DynamicsProcessor(rate, settings);
        var buffer = new float[rate / 100 * 2]; // blocs de 10 ms, com la sortida d'àudio
        float peakIn = 0, peakOut = 0, maxReduction = 0;
        long frames = 0;

        using (source)
        using (var writer = new WaveFileWriter(output, WaveFormat.CreateIeeeFloatWaveFormat(rate, 2)))
        {
            int read;
            while ((read = source.Read(buffer)) > 0)
            {
                var block = buffer.AsSpan(0, read);
                foreach (float v in block) peakIn = Math.Max(peakIn, Math.Abs(v));
                dynamics.Process(block);
                foreach (float v in block) peakOut = Math.Max(peakOut, Math.Abs(v));
                maxReduction = Math.Max(maxReduction, dynamics.TakeMaxReductionDb());
                writer.WriteSamples(buffer, 0, read);
                frames += read / 2;
            }
        }

        double seconds = frames / (double)rate;
        Console.WriteLine();
        Console.WriteLine($"Durada:            {TimeSpan.FromSeconds(seconds):hh\\:mm\\:ss}");
        Console.WriteLine($"Pic d'entrada:     {Db(peakIn):0.0} dBFS");
        Console.WriteLine($"Pic de sortida:    {Db(peakOut):0.0} dBFS (sostre {settings.CeilingDb:0.0})");
        Console.WriteLine($"Abaixada màxima:   {maxReduction:0.0} dB");
        Console.WriteLine($"Temps de càlcul:   {sw.Elapsed.TotalSeconds:0.0} s ({seconds / Math.Max(sw.Elapsed.TotalSeconds, 0.001):0}× el temps real)");
        Console.WriteLine($"Sortida:           {output}");
        return 0;
    }

    private static double Db(float v) => v > 0 ? 20 * Math.Log10(v) : double.NegativeInfinity;
}
