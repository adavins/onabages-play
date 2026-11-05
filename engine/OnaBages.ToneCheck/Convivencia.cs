using System.Diagnostics;
using System.Globalization;
using OnaBages.Audio;

namespace OnaBages.ToneCheck;

/// <summary>
/// Prova de convivència de l'editor (fase 3, pas 5; especificació 9.4): mentre dos enregistradors
/// enregistren un to en temps real, l'editor obre un MP3 llarg, en fa la forma d'ona, hi fa talls
/// i el desa. En acabar es comprova que els enregistraments no han perdut res.
///
/// L'entrada és simulada (<see cref="RealtimeToneInput"/>): un fil que lliura el to al mateix ritme
/// que ho faria una targeta. Així la prova no depèn del maquinari i es pot repetir; el que es
/// prova és que el disc i la CPU que fa servir l'editor no fan perdre mostres als enregistradors.
/// </summary>
public static class Convivencia
{
    public static int Run(double minutes, string folder)
    {
        Directory.CreateDirectory(folder);
        Console.WriteLine($"Carpeta de la prova: {folder}");
        Console.WriteLine();

        // 1. Fitxer llarg per editar (un to; la mida i la durada és el que compta).
        string source = Path.Combine(folder, $"llarg-{minutes:0}min.mp3");
        if (!File.Exists(source))
        {
            Console.WriteLine($"[1] Creant un MP3 de {minutes:0} min per editar (només la primera vegada)...");
            var sw = Stopwatch.StartNew();
            CreateMp3(source, TimeSpan.FromMinutes(minutes));
            Console.WriteLine($"    Fet en {sw.Elapsed.TotalSeconds:F0} s ({new FileInfo(source).Length / 1_048_576.0:F0} MB)");
        }
        else
        {
            Console.WriteLine($"[1] Es fa servir el MP3 ja creat: {source}");
        }

        // 2. Dos enregistradors en marxa, amb una entrada en temps real cadascun.
        Console.WriteLine("[2] Començant dos enregistraments...");
        var recorders = new List<Recorder>();
        for (int i = 1; i <= 2; i++)
        {
            var recorder = new Recorder(i.ToString(), _ => new RealtimeToneInput());
            recorder.SetDevice($"simulada-{i}");
            recorder.Configure($"Convivencia {i}", folder);
            recorder.Start();
            recorders.Add(recorder);
        }
        using var ticking = new CancellationTokenSource();
        var ticker = new Thread(() =>
        {
            // Com el motor: revisió dels enregistradors 10 vegades per segon.
            while (!ticking.IsCancellationRequested)
            {
                foreach (var r in recorders) r.Tick();
                Thread.Sleep(100);
            }
        }) { IsBackground = true };
        ticker.Start();
        Thread.Sleep(3000); // uns segons d'enregistrament abans de començar a editar

        // 3. L'editor, amb la mateixa prioritat baixa que al motor.
        Console.WriteLine("[3] Editor: obrir, forma d'ona, talls i desar (mentre s'enregistra)...");
        string target = Path.Combine(folder, $"llarg-{minutes:0}min (editat).mp3");
        string? editorError = null;
        var times = new List<(string Step, TimeSpan Time)>();
        int expectedFrames = 0, savedFrames = 0;
        var editor = new Thread(() =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var index = Mp3FrameIndex.Build(source);
                times.Add(("Obrir (indexar)", sw.Elapsed));

                sw.Restart();
                var peaks = new byte[index.FrameCount * WaveformPeaks.BytesPerFrame];
                WaveformPeaks.Compute(source, index.SampleRate, index.SamplesPerFrame, index.FrameCount, peaks);
                times.Add(("Forma d'ona", sw.Elapsed));

                // Talls com els d'un programa: l'inici, un tros del mig i el final.
                var edit = new Mp3Edit(index);
                int second = (int)Math.Round(1 / index.FrameSeconds);
                edit.Delete(0, 30 * second);
                edit.Delete(edit.FrameCount / 2, edit.FrameCount / 2 + 120 * second);
                edit.Delete(edit.FrameCount - 20 * second, edit.FrameCount);
                expectedFrames = edit.FrameCount;

                sw.Restart();
                edit.Save(target);
                times.Add(("Desar", sw.Elapsed));
                savedFrames = Mp3FrameIndex.Build(target).FrameCount;
            }
            catch (Exception ex)
            {
                editorError = ex.Message;
            }
        })
        { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Editor" };
        editor.Start();
        editor.Join();
        Thread.Sleep(3000); // uns segons més d'enregistrament després de desar

        // 4. Aturar i comprovar.
        Console.WriteLine("[4] Aturant els enregistraments i comprovant-los...");
        ticking.Cancel();
        ticker.Join();
        foreach (var r in recorders) r.Stop();

        bool ok = true;
        Console.WriteLine();
        foreach (var (step, time) in times)
            Console.WriteLine($"  Editor · {step,-16} {time.TotalSeconds,7:F1} s");
        if (editorError is not null)
        {
            ok = false;
            Console.WriteLine($"  Editor · ERROR: {editorError}");
        }
        else if (savedFrames != expectedFrames)
        {
            ok = false;
            Console.WriteLine($"  Editor · el fitxer desat té {savedFrames} fotogrames i n'havia de tenir {expectedFrames}");
        }
        else
        {
            Console.WriteLine($"  Editor · fitxer desat correcte ({savedFrames} fotogrames)");
        }

        foreach (var r in recorders)
        {
            Console.WriteLine();
            Console.WriteLine($"  Enregistrador {r.Id}: {Path.GetFileName(r.FilePath)} ({r.Elapsed.TotalSeconds:F1} s)");
            Console.WriteLine($"    Mostres perdudes (disc lent):  {r.SamplesLost}");
            if (r.SamplesLost > 0) ok = false;

            var report = ToneAnalyzer.AnalyzeFile(r.FilePath!);
            // Es compten ones: tot el que s'ha capturat ha d'arribar al fitxer.
            double missingMs = report.MissingMs(r.Elapsed.TotalSeconds);
            Console.WriteLine($"    Talls o clics al fitxer:       {report.Events.Count}{(report.TooManyEvents ? "+" : "")}");
            Console.WriteLine($"    Àudio que falta al fitxer:     {missingMs:F1} ms");
            foreach (var e in report.Events.Take(5))
                Console.WriteLine($"      {(e.Problem == ToneProblem.Gap ? "TALL" : "CLIC")} a {e.Position.TotalSeconds:F2} s");
            if (!report.Ok || Math.Abs(missingMs) > 30) ok = false;
        }

        Console.WriteLine();
        Console.WriteLine(ok
            ? "RESULTAT: CORRECTE. L'editor ha treballat sense fer perdre res als enregistraments."
            : "RESULTAT: HI HA PROBLEMES (vegeu més amunt).");
        Console.WriteLine($"Els fitxers de la prova són a {folder} (es poden esborrar).");
        foreach (var r in recorders) r.Dispose();
        return ok ? 0 : 1;
    }

    /// <summary>MP3 de 320 kbps amb el to de prova, sense carregar-lo sencer a la memòria.</summary>
    private static void CreateMp3(string path, TimeSpan length)
    {
        const int rate = 48000;
        var tone = new ToneGenerator(rate);
        using var encoder = new LameEncoder(rate, 320);
        using var file = File.Create(path);
        var buffer = new float[rate * 2]; // 1 s
        for (long done = 0; done < (long)length.TotalSeconds; done++)
        {
            tone.Fill(buffer);
            file.Write(encoder.Encode(buffer, buffer.Length));
        }
        file.Write(encoder.Flush());
    }
}

/// <summary>
/// Entrada simulada que lliura el to de prova en paquets de 10 ms, al ritme real
/// (com el fil de captura de Windows), des d'un fil d'alta prioritat.
/// </summary>
public sealed class RealtimeToneInput : IAudioInput
{
    private const int Rate = 48000;
    private Thread? _thread;
    private volatile bool _stop;

    public int SampleRate => Rate;
    public int Channels => 2;
    public event SamplesCapturedHandler? DataAvailable;
    /// <summary>L'entrada simulada no s'atura mai sola (ni es desconnecta).</summary>
    public event Action<Exception?>? Stopped { add { } remove { } }

    public void Start()
    {
        _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "Entrada simulada" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join();
        _thread = null;
    }

    public void Dispose() => Stop();

    private void Run()
    {
        var tone = new ToneGenerator(Rate);
        var packet = new float[Rate / 100 * 2];
        var clock = Stopwatch.StartNew();
        long sent = 0; // paquets lliurats
        while (!_stop)
        {
            // Lliura tots els paquets que «toquen» segons el rellotge (si el fil s'ha endarrerit, es recupera).
            long due = clock.ElapsedMilliseconds / 10;
            while (sent < due && !_stop)
            {
                tone.Fill(packet);
                DataAvailable?.Invoke(packet, false, false);
                sent++;
            }
            Thread.Sleep(2);
        }
    }
}
