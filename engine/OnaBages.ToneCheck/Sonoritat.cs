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

            var duration = source.Duration;
            source.Dispose();
            var analysis = TrackLoudness.Measure(path);
            double? lufs = analysis.Lufs;
            double peakDb = analysis.Peak > 0 ? 20 * Math.Log10(analysis.Peak) : double.NegativeInfinity;
            string gain = lufs is { } l ? $"{Math.Min(MaxBoostDb, TargetLufs - l):+0.0;-0.0} dB" : "—";
            Console.WriteLine($"{Path.GetFileName(path)}");
            Console.WriteLine($"  Sonoritat: {(lufs is { } v ? $"{v:0.0} LUFS" : "silenci")}   Pic: {peakDb:0.0} dBFS   Guany: {gain}   ({sw.Elapsed.TotalSeconds:0.0} s)");
            // Retall de silencis (fase 5): quant silenci hi ha al principi i al final, per a cada llindar.
            foreach (var b in analysis.Sound ?? [])
                Console.WriteLine($"  Silenci a {b.ThresholdDb} dBFS: {b.Start * 1000,7:0.0} ms al principi, {(duration.TotalSeconds - b.End) * 1000,7:0.0} ms al final");
        }
        return result;
    }
}
