using System.Globalization;
using System.Text;
using OnaBages.Audio;
using OnaBages.ToneCheck;

// Eina de la prova de to (fase 1, pas 8).
//   generar <fitxer.wav> <minuts>            crea el to de prova
//   analitzar <fitxer> [segons esperats]     busca talls en un enregistrament del to
//   convivencia [minuts] [carpeta]           editor + dos enregistradors alhora (fase 3, pas 5)
//   processar <entrada> <sortida.wav>        passa un fitxer pel compressor/limitador (fase 4, pas 4a)
//   sonoritat <fitxer> [fitxer...]           mesura la sonoritat (LUFS) i el pic (fase 4, pas 5a)
//   emetre <shoutcast|icecast> <servidor> <port> <sid|punt de muntatge> [segons] [kbps] [Hz]
//                                            envia un to a un servidor d'streaming (fase 5, pas 31b)

Console.OutputEncoding = Encoding.UTF8;

if (args.Length >= 3 && args[0] == "generar")
    return Generate(args[1], args[2]);
if (args.Length >= 2 && args[0] == "analitzar")
    return Analyze(args[1], args.Length >= 3 ? args[2] : null);
if (args.Length >= 1 && args[0] == "convivencia")
{
    double minutes = 60;
    if (args.Length >= 2 && (!double.TryParse(args[1].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out minutes) || minutes < 1))
    {
        Console.Error.WriteLine($"Minuts no vàlids: {args[1]}");
        return 2;
    }
    string folder = args.Length >= 3 ? Path.GetFullPath(args[2]) : Path.Combine(Path.GetTempPath(), "OnaBagesConvivencia");
    return Convivencia.Run(minutes, folder);
}
if (args.Length >= 2 && args[0] == "sonoritat")
    return Sonoritat.Run(args.Skip(1).Select(Path.GetFullPath));
if (args.Length >= 3 && args[0] == "processar")
{
    // Ajustos: per defecte, els del SqrSoft de La Central; «desactivat» per comparar (el so tal qual).
    var settings = args.Length >= 4 && args[3] == "desactivat" ? new DynamicsSettings() : DynamicsSettings.SqrSoftLaCentral;
    return Processar.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), settings);
}

if (args.Length >= 5 && args[0] == "emetre")
{
    var type = args[1].Equals("icecast", StringComparison.OrdinalIgnoreCase) ? StreamServerType.Icecast : StreamServerType.Shoutcast;
    if (!int.TryParse(args[3], out int port)) { Console.Error.WriteLine($"Port no vàlid: {args[3]}"); return 2; }
    int seconds = args.Length >= 6 && int.TryParse(args[5], out int s) ? s : 30;
    int kbps = args.Length >= 7 && int.TryParse(args[6], out int k) ? k : 128;
    int hz = args.Length >= 8 && int.TryParse(args[7], out int h) ? h : 44100;
    return Emetre.Run(type, args[2], port, args[4], seconds, kbps, hz);
}

Console.WriteLine("""
    Ús:
      OnaBages.ToneCheck generar <fitxer.wav> <minuts>
      OnaBages.ToneCheck analitzar <fitxer.mp3> [segons que havia de durar el to]
      OnaBages.ToneCheck convivencia [minuts, per defecte 60] [carpeta]
      OnaBages.ToneCheck processar <entrada> <sortida.wav> [desactivat]   compressor/limitador (SqrSoft de La Central)
      OnaBages.ToneCheck sonoritat <fitxer> [fitxer...]                  sonoritat (LUFS, EBU R128) i pic
      OnaBages.ToneCheck emetre <shoutcast|icecast> <servidor> <port> <sid|punt de muntatge> [segons=30] [kbps=128] [Hz=44100]
    """);
return 2;

static int Generate(string path, string minutesText)
{
    if (!double.TryParse(minutesText.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes) || minutes <= 0)
    {
        Console.Error.WriteLine($"Minuts no vàlids: {minutesText}");
        return 2;
    }

    path = Path.GetFullPath(path);
    Console.WriteLine($"Generant {minutes} min de to de {ToneGenerator.Frequency} Hz (−12 dBFS, 48 kHz, 16 bits)...");
    ToneGenerator.WriteWav(path, TimeSpan.FromMinutes(minutes));
    Console.WriteLine($"Fet: {path} ({new FileInfo(path).Length / 1_048_576.0:F0} MB)");
    Console.WriteLine($"Durada exacta: {minutes * 60:F3} s");
    return 0;
}

static int Analyze(string path, string? expectedText)
{
    double? expected = null;
    if (expectedText is not null)
    {
        if (!double.TryParse(expectedText.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double e) || e <= 0)
        {
            Console.Error.WriteLine($"Segons no vàlids: {expectedText}");
            return 2;
        }
        expected = e;
    }

    Console.WriteLine($"Analitzant {path}...");
    ToneReport report;
    try
    {
        report = ToneAnalyzer.AnalyzeFile(path, done => Console.WriteLine($"  {done:P0}"));
    }
    catch (AudioFileException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }

    Console.WriteLine();
    Console.WriteLine($"Fitxer:     {Time(report.FileLength)}");
    if (!report.ToneFound)
    {
        Console.WriteLine("RESULTAT: no s'hi troba el to de prova (el fitxer és en silenci).");
        return 1;
    }

    Console.WriteLine($"To:         de {Time(report.ToneStart)} a {Time(report.ToneStart + report.ToneLength)} ({report.ToneLength.TotalSeconds:F3} s)");
    Console.WriteLine($"Nivell:     {report.LevelDb:F1} dBFS (el to generat fa −15,0)");

    // Amb menys de 2 minuts de to, uns pocs ms d'imprecisió a les vores ja donen centenars de ppm.
    if (report.ToneLength >= TimeSpan.FromMinutes(2))
        Console.WriteLine($"Rellotges:  {report.ClockPpm:+0;-0;0} ppm de diferència entre el de sortida i el d'entrada (fins a ±200 és normal)");
    else
        Console.WriteLine("Rellotges:  el to és massa curt per mesurar la diferència de rellotges");

    bool ok = report.Ok;
    if (expected is { } seconds)
    {
        // Es compten ones, no temps: una diferència de rellotges no és àudio perdut.
        long expectedCycles = (long)Math.Round(seconds * ToneGenerator.Frequency);
        double missingMs = report.MissingMs(seconds);
        Console.WriteLine($"Ones:       {report.Cycles} de {expectedCycles} esperades ({-missingMs:+0;-0;0} ms)");
        // Tolerància: les vores del to es mesuren al mil·lisegon.
        if (Math.Abs(missingMs) > 3)
        {
            ok = false;
            Console.WriteLine(missingMs > 0
                ? report.Events.Count == 0
                    ? "  → hi falta àudio, i no hi ha cap clic enmig: s'ha perdut a l'inici o al final del to"
                    : "  → hi falta àudio: s'han perdut mostres en algun punt"
                : "  → hi sobra àudio: s'ha repetit o inserit alguna cosa");
        }
    }

    Console.WriteLine();
    if (report.TooManyEvents)
        Console.WriteLine($"Massa problemes (més de {ToneAnalyzer.MaxEvents}): segurament no és el to de prova, o l'entrada no és la correcta.");

    foreach (var e in report.Events)
    {
        Console.WriteLine(e.Problem == ToneProblem.Gap
            ? $"  TALL  a {Time(e.Position)}: {e.Length.TotalMilliseconds:F0} ms sense so"
            : $"  CLIC  a {Time(e.Position)}: salt a l'ona (àudio perdut o repetit)");
    }

    Console.WriteLine(ok ? "RESULTAT: CORRECTE, sense talls." : "RESULTAT: HI HA PROBLEMES.");
    return ok ? 0 : 1;
}

static string Time(TimeSpan t) => t.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
