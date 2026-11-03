namespace OnaBages.Audio;

/// <summary>Tipus de problema trobat en un enregistrament del to de prova.</summary>
public enum ToneProblem
{
    /// <summary>El to baixa o desapareix (p. ex. el reproductor s'ha quedat sense dades).</summary>
    Gap,
    /// <summary>Salt brusc de l'ona: s'ha perdut o repetit un trosset d'àudio.</summary>
    Click,
}

/// <summary>Un problema, amb la posició dins del fitxer.</summary>
public sealed record ToneEvent(ToneProblem Problem, TimeSpan Position, TimeSpan Length);

/// <summary>Resultat de l'anàlisi.</summary>
/// <param name="FileLength">Durada de tot el fitxer analitzat.</param>
/// <param name="ToneFound">S'hi ha trobat el to de prova.</param>
/// <param name="ToneStart">On comença el to dins del fitxer.</param>
/// <param name="ToneLength">Durada del to, de principi a fi.</param>
/// <param name="LevelDb">Nivell del to (dBFS, RMS). El to generat fa −15 dBFS.</param>
/// <param name="Events">Problemes trobats dins del to (com a molt <see cref="ToneAnalyzer.MaxEvents"/>).</param>
/// <param name="TooManyEvents">N'hi ha més dels que es llisten: probablement no és el to de prova.</param>
/// <param name="Cycles">Ones (cicles) del to comptades al fitxer.</param>
public sealed record ToneReport(
    TimeSpan FileLength,
    bool ToneFound,
    TimeSpan ToneStart,
    TimeSpan ToneLength,
    double LevelDb,
    IReadOnlyList<ToneEvent> Events,
    bool TooManyEvents,
    long Cycles = 0)
{
    public bool Ok => ToneFound && Events.Count == 0 && !TooManyEvents;

    /// <summary>
    /// Àudio que falta (ms) respecte a un to de <paramref name="expectedSeconds"/> segons, comptant ones.
    /// Negatiu si en sobra. No depèn de la velocitat dels rellotges: si es perd àudio, falten ones;
    /// si un rellotge va una mica més ràpid, hi són totes però duren una mica menys.
    /// </summary>
    public double MissingMs(double expectedSeconds) =>
        (expectedSeconds * ToneGenerator.Frequency - Cycles) * 1000.0 / ToneGenerator.Frequency;

    /// <summary>
    /// Diferència de velocitat entre el rellotge que ha reproduït i el que ha enregistrat,
    /// en milionèsimes (ppm). Entre dos rellotges de quars, fins a uns ±200 ppm és normal.
    /// </summary>
    public double ClockPpm =>
        ToneLength > TimeSpan.Zero
            ? (Cycles / ToneLength.TotalSeconds / ToneGenerator.Frequency - 1) * 1_000_000
            : 0;
}

/// <summary>
/// Analitza un enregistrament del to de prova (<see cref="ToneGenerator"/>) i hi busca
/// talls. Fa dues comprovacions:
/// <list type="bullet">
/// <item><b>Silencis:</b> el nivell es mira cada mil·lisegon; si baixa a menys d'una quarta part
/// del normal (−12 dB) enmig del to, és un tall.</item>
/// <item><b>Clics:</b> en una ona sinusoïdal pura, cada mostra es pot calcular a partir de les
/// dues anteriors. Si la mostra real se n'aparta molt, hi ha un salt: s'ha perdut o repetit àudio.</item>
/// </list>
/// Es fa servir a trossos (<see cref="Process"/>) perquè un fitxer de 3 hores no càpiga sencer a la memòria.
/// </summary>
public sealed class ToneAnalyzer
{
    /// <summary>Un tall és quan el nivell baixa d'aquesta proporció del normal (−12 dB).</summary>
    public const double GapRatio = 0.25;

    /// <summary>Un clic és quan la mostra s'aparta més d'aquesta proporció de l'amplitud.</summary>
    public const double ClickRatio = 0.05;

    /// <summary>Per sota d'aquest nivell es considera silenci (−60 dBFS): abans i després del to.</summary>
    public const double SilenceAmplitude = 0.001;

    /// <summary>Marge a l'inici i al final del to on no es busquen problemes (el to hi comença o s'hi talla).</summary>
    public static readonly TimeSpan EdgeMargin = TimeSpan.FromMilliseconds(30);

    public const int MaxEvents = 100;

    private readonly int _sampleRate;
    private readonly int _windowFrames; // mostres per mil·lisegon
    private readonly double _coefficient;

    // Nivell (RMS) de cada mil·lisegon. 3 hores ≈ 10,8 milions de valors ≈ 43 MB.
    private readonly List<float> _levels = [];
    // Ones (pas per zero cap amunt) de cada mil·lisegon: 0, 1 o 2. ≈ 11 MB per 3 hores.
    private readonly List<byte> _crossings = [];
    private readonly List<long> _clicks = [];
    private bool _tooManyClicks;

    private long _frame;
    private double _sum;
    private int _inWindow;
    private double _amplitude; // amplitud del mil·lisegon anterior
    private float _prev1, _prev2;
    private int _windowCrossings;
    private bool _belowZero; // l'ona ha baixat prou: la propera pujada és una ona nova

    public ToneAnalyzer(int sampleRate)
    {
        _sampleRate = sampleRate;
        _windowFrames = Math.Max(1, sampleRate / 1000);
        _coefficient = 2 * Math.Cos(2 * Math.PI * ToneGenerator.Frequency / sampleRate);
    }

    /// <summary>Afegeix un bloc d'àudio estèreo intercalat.</summary>
    public void Process(ReadOnlySpan<float> stereo)
    {
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            float x = (stereo[i] + stereo[i + 1]) / 2;

            // Sinusoide pura: x[n] + x[n-2] = 2·cos(ω)·x[n-1]. La diferència ha de ser gairebé zero.
            if (_frame >= 2 && _amplitude > SilenceAmplitude)
            {
                double residual = x + _prev2 - _coefficient * _prev1;
                if (Math.Abs(residual) > ClickRatio * _amplitude)
                    AddClick(_frame - 1);
            }
            _prev2 = _prev1;
            _prev1 = x;

            // Comptador d'ones, amb un marge (30 % de l'amplitud) perquè el soroll no en compti de més.
            double margin = 0.3 * _amplitude;
            if (x < -margin)
                _belowZero = true;
            else if (_belowZero && x > margin)
            {
                _belowZero = false;
                _windowCrossings++;
            }

            _sum += x * x;
            if (++_inWindow == _windowFrames)
            {
                double rms = Math.Sqrt(_sum / _windowFrames);
                _levels.Add((float)rms);
                _crossings.Add((byte)Math.Min(_windowCrossings, 255));
                _windowCrossings = 0;
                _amplitude = rms * Math.Sqrt(2);
                _sum = 0;
                _inWindow = 0;
            }
            _frame++;
        }
    }

    private void AddClick(long frame)
    {
        // Les mostres d'un mateix salt (pocs ms) compten com un sol clic.
        if (_clicks.Count > 0 && frame - _clicks[^1] < _sampleRate / 200)
            return;
        if (_clicks.Count >= 100_000)
        {
            _tooManyClicks = true;
            return;
        }
        _clicks.Add(frame);
    }

    /// <summary>Acaba l'anàlisi i en retorna el resultat.</summary>
    public ToneReport Finish()
    {
        var fileLength = FramesToTime(_frame);

        // Nivell normal del to: la mediana dels mil·lisegons que no són silenci.
        var loud = _levels.Where(l => l > SilenceAmplitude / Math.Sqrt(2)).ToArray();
        if (loud.Length == 0)
            return new ToneReport(fileLength, false, TimeSpan.Zero, TimeSpan.Zero, double.NegativeInfinity, [], false);
        Array.Sort(loud);
        double reference = loud[loud.Length / 2];

        // On comença i on acaba el to: primer i últim mil·lisegon amb més de mig nivell.
        int first = _levels.FindIndex(l => l >= reference / 2);
        int last = _levels.FindLastIndex(l => l >= reference / 2);
        var toneStart = WindowsToTime(first);
        var toneEnd = WindowsToTime(last + 1);
        var checkFrom = toneStart + EdgeMargin;
        var checkTo = toneEnd - EdgeMargin;

        var events = new List<ToneEvent>();

        // Silencis: mil·lisegons seguits per sota del llindar, enmig del to.
        for (int w = first; w <= last; w++)
        {
            if (_levels[w] >= GapRatio * reference)
                continue;
            int start = w;
            while (w <= last && _levels[w] < GapRatio * reference)
                w++;
            var position = WindowsToTime(start);
            if (position >= checkFrom && position <= checkTo)
                events.Add(new ToneEvent(ToneProblem.Gap, position, WindowsToTime(w - start)));
        }

        // Clics, si no són part d'un silenci (les vores d'un silenci també fan salt).
        var near = TimeSpan.FromMilliseconds(5);
        var gaps = events.ToList();
        foreach (long frame in _clicks)
        {
            var position = FramesToTime(frame);
            if (position < checkFrom || position > checkTo)
                continue;
            if (gaps.Any(g => position >= g.Position - near && position <= g.Position + g.Length + near))
                continue;
            events.Add(new ToneEvent(ToneProblem.Click, position, TimeSpan.Zero));
        }

        events.Sort((a, b) => a.Position.CompareTo(b.Position));
        bool tooMany = _tooManyClicks || events.Count > MaxEvents;
        return new ToneReport(
            fileLength,
            ToneFound: true,
            toneStart,
            toneEnd - toneStart,
            LevelDb: 20 * Math.Log10(reference),
            events.Take(MaxEvents).ToList(),
            tooMany,
            Cycles: CountCycles(first, last));
    }

    /// <summary>Analitza un fitxer sencer (MP3, WAV...).</summary>
    /// <param name="progress">Opcional: rep la proporció feta (0..1) de tant en tant.</param>
    public static ToneReport AnalyzeFile(string path, Action<double>? progress = null)
    {
        const int sampleRate = 48000;
        using var source = AudioFileSource.Open(path, sampleRate);
        var analyzer = new ToneAnalyzer(sampleRate);
        var buffer = new float[sampleRate * 2]; // 1 s
        double total = Math.Max(1, source.Duration.TotalSeconds);
        long seconds = 0;

        int read;
        while ((read = source.Read(buffer)) > 0)
        {
            analyzer.Process(buffer.AsSpan(0, read));
            if (++seconds % 60 == 0)
                progress?.Invoke(Math.Min(1, seconds / total));
        }
        return analyzer.Finish();
    }

    private long CountCycles(int firstWindow, int lastWindow)
    {
        long cycles = 0;
        for (int w = firstWindow; w <= lastWindow; w++)
            cycles += _crossings[w];
        return cycles;
    }

    private TimeSpan FramesToTime(long frames) => TimeSpan.FromSeconds((double)frames / _sampleRate);

    private TimeSpan WindowsToTime(long windows) => FramesToTime(windows * _windowFrames);
}
