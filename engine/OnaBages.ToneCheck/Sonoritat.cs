using System.Diagnostics;
using OnaBages.Audio;

namespace OnaBages.ToneCheck;

/// <summary>
/// Mesura la sonoritat (LUFS, EBU R128) i el pic d'un o més fitxers (fase 4, pas 5a; D-070),
/// i diu quin guany s'hi aplicaria per arribar a l'objectiu. Per comparar-ho amb altres programes
/// (p. ex. el ReplayGain del foobar2000 o «Mesura de sonoritat» de l'Audacity).
/// </summary>
public static class Sonoritat
{
    public const double TargetLufs = -12;
    public const double MaxBoostDb = 10;

    public static int Run(IEnumerable<string> paths)
    {
        int result = 0;
        Console.WriteLine($"Objectiu: {TargetLufs} LUFS (guany màxim +{MaxBoostDb} dB)");
        Console.WriteLine();
        foreach (string path in paths)
        {
            const int rate = 48000;
            var sw = Stopwatch.StartNew();
            AudioFileSource source;
            try
            {
                source = AudioFileSource.Open(path, rate);
            }
            catch (AudioFileException ex)
            {
                Console.WriteLine($"{Path.GetFileName(path)}: {ex.Message}");
                result = 2;
                continue;
            }

            var meter = new LoudnessMeter(rate);
            var buffer = new float[rate * 2];
            using (source)
            {
                int read;
                while ((read = source.Read(buffer)) > 0)
                    meter.Add(buffer.AsSpan(0, read));
            }

            double? lufs = meter.IntegratedLufs;
            double peakDb = meter.SamplePeak > 0 ? 20 * Math.Log10(meter.SamplePeak) : double.NegativeInfinity;
            string gain = lufs is { } l ? $"{Math.Min(MaxBoostDb, TargetLufs - l):+0.0;-0.0} dB" : "—";
            Console.WriteLine($"{Path.GetFileName(path)}");
            Console.WriteLine($"  Sonoritat: {(lufs is { } v ? $"{v:0.0} LUFS" : "silenci")}   Pic: {peakDb:0.0} dBFS   Guany: {gain}   ({sw.Elapsed.TotalSeconds:0.0} s)");
        }
        return result;
    }
}
