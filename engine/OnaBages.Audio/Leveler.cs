namespace OnaBages.Audio;

/// <summary>
/// Anivellador (fase 5, pas 30a; D-134): iguala el volum de la parla d'una pista mentre sona, com un tècnic que mou el
/// fader. Pensat per a entrevistes on una veu és molt fluixa i l'altra molt forta. És el contrari d'un expansor: redueix
/// la diferència entre els trossos fluixos i els forts.
///
/// Es posa a sobre de la lectura d'una pista (fil de lectura, mai el d'àudio): llegeix <see cref="LookaheadSeconds"/> per
/// endavant i, per a cada bloc de 100 ms, mira el nivell mitjà (sonoritat a curt termini, en LUFS, amb el filtre K de la
/// normalització) d'una finestra de ±1 s al seu voltant. Calcula el guany que el portaria a l'objectiu (com a molt
/// <c>maxBoostDb</c> amunt i <c>maxCutDb</c> avall) i hi va a poc a poc (6 dB/s amunt i 12 dB/s avall), amb una rampa
/// dins de cada bloc perquè no faci clics.
/// - Els blocs per sota de <see cref="GateLufs"/> (pauses, soroll de fons) no compten: si la finestra només en té
///   d'aquests, el guany no canvia (no puja el soroll entre frases).
/// - No satura: com que sap el pic del que ve, no puja tant que passi de <see cref="CeilingDb"/>.
/// - El nivell es mesura amb el canal més fort com si sonés pels dos: una pista amb un canal buit (que sonarà en mono,
///   D-132) no es puja 3 dB de més.
/// </summary>
public sealed class Leveler
{
    public const double LookaheadSeconds = 1.0;
    public const double WindowSeconds = 1.0;          // a cada costat del bloc
    public const double GateLufs = -50;
    public const double CeilingDb = -1;
    public const double UpDbPerSecond = 6, DownDbPerSecond = 12;
    public const double DefaultMaxBoostDb = 12, MaxMaxBoostDb = 20, DefaultMaxCutDb = 12;
    private const double PeakGuardSeconds = 0.3;
    /// <summary>Com la normalització (BS.1770): els blocs 10 dB per sota de la mitjana de la finestra no compten.</summary>
    private const double RelativeGateDb = -10;
    /// <summary>Blocs de veu que ha de tenir la finestra perquè el guany es torni a calcular (si no, es manté).</summary>
    private const int MinSpeechBlocks = 3;

    private readonly Func<Span<float>, int> _source;
    private readonly int _blockFrames;
    private readonly int _lookahead, _window, _peakGuard;
    private readonly double _maxCutDb;
    // Es poden canviar mentre treballa (SetLimits, des d'un altre fil): es llegeixen amb Volatile.
    private double _targetLufs, _maxBoostDb;
    private readonly double _upPerBlock, _downPerBlock;
    private readonly LoudnessMeter.Biquad[] _shelf = new LoudnessMeter.Biquad[2];
    private readonly LoudnessMeter.Biquad[] _highPass = new LoudnessMeter.Biquad[2];

    // Per a cada bloc de 100 ms llegit (des del principi): energia ponderada (filtre K) i pic.
    private readonly List<double> _energy = [];
    private readonly List<float> _peak = [];
    private readonly List<int> _lengths = []; // mostres de cada bloc (l'últim pot ser més curt)
    // Mostres dels blocs llegits que encara no han sortit (el primer és el bloc _next).
    private readonly Queue<float[]> _samples = new();
    private readonly Queue<float[]> _free = new();
    private bool _sourceDone;
    private int _next;              // bloc que surt ara
    private double _gainDb;         // guany al final de l'últim bloc que ha sortit
    private double _desiredDb;      // l'últim guany volgut (es manté a les pauses)
    private bool _started;
    private bool _holding;          // ara no hi ha veu a prop: el guany es manté (per a l'indicador; D-149)
    private float[] _out = [];
    private int _outPos, _outLen;

    /// <param name="source">D'on surten les mostres (estèreo intercalat). 0 = s'ha acabat.</param>
    /// <param name="sampleRate">Freqüència de les mostres.</param>
    /// <param name="targetLufs">Nivell al qual es porta la parla (el mateix objectiu que la normalització).</param>
    public Leveler(Func<Span<float>, int> source, int sampleRate, double targetLufs,
        double maxBoostDb = DefaultMaxBoostDb, double maxCutDb = DefaultMaxCutDb)
    {
        _source = source;
        _blockFrames = sampleRate / 10;
        _lookahead = (int)Math.Round(LookaheadSeconds * 10);
        _window = (int)Math.Round(WindowSeconds * 10);
        _peakGuard = (int)Math.Ceiling(PeakGuardSeconds * 10);
        _targetLufs = targetLufs;
        _maxBoostDb = Math.Clamp(maxBoostDb, 0, MaxMaxBoostDb);
        _maxCutDb = Math.Max(0, maxCutDb);
        _upPerBlock = UpDbPerSecond / 10;
        _downPerBlock = DownDbPerSecond / 10;
        for (int c = 0; c < 2; c++)
        {
            _shelf[c] = LoudnessMeter.Biquad.HighShelf(sampleRate, 1681.974450955533, 3.999843853973347, 0.7071752369554196);
            _highPass[c] = LoudnessMeter.Biquad.HighPass(sampleRate, 38.13547087602444, 0.5003270373238773);
        }
    }

    /// <summary>
    /// Canvia l'objectiu i el màxim mentre treballa (fase 5, D-147): el guany hi va a poc a poc, amb les velocitats de sempre
    /// (sense salts). Es pot cridar des d'un altre fil.
    /// </summary>
    public void SetLimits(double targetLufs, double maxBoostDb)
    {
        if (double.IsFinite(targetLufs)) Volatile.Write(ref _targetLufs, targetLufs);
        if (double.IsFinite(maxBoostDb)) Volatile.Write(ref _maxBoostDb, Math.Clamp(maxBoostDb, 0, MaxMaxBoostDb));
    }

    /// <summary>Guany que s'aplica ara (dB, al final de l'últim bloc que ha sortit). Per a l'indicador.</summary>
    public double CurrentGainDb => Volatile.Read(ref _gainDb);

    /// <summary>
    /// En pausa: al tros que surt ara no hi ha veu a prop (silenci o soroll de fons), i el guany es manté on era (no puja).
    /// Per a l'indicador (D-149).
    /// </summary>
    public bool Holding => Volatile.Read(ref _holding);

    /// <summary>Omple <paramref name="buffer"/> amb mostres anivellades. 0 = s'ha acabat la pista.</summary>
    public int Read(Span<float> buffer)
    {
        int written = 0;
        while (written < buffer.Length)
        {
            if (_outPos < _outLen)
            {
                int n = Math.Min(buffer.Length - written, _outLen - _outPos);
                _out.AsSpan(_outPos, n).CopyTo(buffer[written..]);
                _outPos += n;
                written += n;
                continue;
            }
            if (!NextBlock()) break;
        }
        return written;
    }

    /// <summary>Prepara el bloc següent (llegeix el que calgui per endavant). False si ja no n'hi ha més.</summary>
    private bool NextBlock()
    {
        while (!_sourceDone && _energy.Count - _next <= _lookahead) ReadBlock();
        if (_next >= _energy.Count) return false;

        double target = GainFor(_next);
        double from = _started ? _gainDb : target; // el primer bloc ja comença al guany bo (sense pujar des de 0 dB)
        _started = true;

        var block = _samples.Dequeue();
        int samples = BlockLength(_next);
        double fromLinear = Math.Pow(10, from / 20), toLinear = Math.Pow(10, target / 20);
        int frames = samples / 2;
        for (int f = 0; f < frames; f++)
        {
            float g = (float)(fromLinear + (toLinear - fromLinear) * (f + 1) / frames);
            block[f * 2] *= g;
            block[f * 2 + 1] *= g;
        }
        if (_out.Length > 0 && _out.Length == block.Length) _free.Enqueue(_out);
        _out = block;
        _outPos = 0;
        _outLen = samples;
        Volatile.Write(ref _gainDb, target);
        _next++;
        return true;
    }

    /// <summary>Guany per al bloc <paramref name="i"/>: cap a l'objectiu, sense passar dels límits ni de la velocitat ni saturar.</summary>
    private double GainFor(int i)
    {
        // Nivell de la parla de la finestra, com la sonoritat integrada (BS.1770) però només dins de la finestra:
        // blocs de 400 ms (un de nou cada 100 ms), sense els de sota la porta (pauses) ni els 10 dB per sota de la mitjana.
        int first = Math.Max(3, i - _window), last = Math.Min(_energy.Count - 1, i + _window);
        double absoluteGate = EnergyFor(GateLufs);
        double sum = 0;
        int count = 0;
        for (int j = first; j <= last; j++)
        {
            double e = Block400(j);
            if (e >= absoluteGate) { sum += e; count++; }
        }
        if (count >= MinSpeechBlocks)
        {
            double relativeGate = sum / count * Math.Pow(10, RelativeGateDb / 10);
            double speech = 0;
            int speechCount = 0;
            for (int j = first; j <= last; j++)
            {
                double e = Block400(j);
                if (e >= absoluteGate && e >= relativeGate) { speech += e; speechCount++; }
            }
            if (speechCount >= MinSpeechBlocks)
            {
                double level = -0.691 + 10 * Math.Log10(speech / speechCount);
                _desiredDb = Volatile.Read(ref _targetLufs) - level;
            }
        }
        // Dins dels límits (també a les pauses: si s'abaixa el màxim mentre treballa, el guany hi baixa de seguida).
        _desiredDb = Math.Clamp(_desiredDb, -_maxCutDb, Volatile.Read(ref _maxBoostDb));
        // (Amb poca parla a la finestra, o cap, es manté l'últim guany volgut: a les pauses no puja.)

        double gain = !_started ? _desiredDb
            : Math.Clamp(_desiredDb, _gainDb - _downPerBlock, _gainDb + _upPerBlock);

        // A la pausa mateix (sense veu a ±0,2 s) el guany no puja: així no s'aixeca el soroll entre frases.
        bool speechHere = false;
        for (int j = Math.Max(3, i - 2); j <= Math.Min(_energy.Count - 1, i + 2) && !speechHere; j++)
            speechHere = Block400(j) >= absoluteGate;
        if (_started && !speechHere && gain > _gainDb) gain = _gainDb;
        Volatile.Write(ref _holding, !speechHere);

        // No saturar: el pic d'aquest bloc i dels que venen de seguida, amb el guany, no pot passar del sostre.
        float peak = 0;
        for (int j = i; j <= Math.Min(_peak.Count - 1, i + _peakGuard); j++) peak = Math.Max(peak, _peak[j]);
        if (peak > 0) gain = Math.Min(gain, CeilingDb - 20 * Math.Log10(peak));
        return gain;
    }

    private int BlockLength(int i) => _lengths[i];

    /// <summary>Energia mitjana del bloc de 400 ms que acaba al bloc de 100 ms <paramref name="j"/> (j ≥ 3).</summary>
    private double Block400(int j) => (_energy[j] + _energy[j - 1] + _energy[j - 2] + _energy[j - 3]) / 4;

    private static double EnergyFor(double lufs) => Math.Pow(10, (lufs + 0.691) / 10);

    /// <summary>Llegeix un bloc de 100 ms (o el que quedi) i en mesura la sonoritat i el pic.</summary>
    private void ReadBlock()
    {
        var block = _free.Count > 0 ? _free.Dequeue() : new float[_blockFrames * 2];
        int got = 0, r;
        while (got < block.Length && (r = _source(block.AsSpan(got))) > 0) got += r;
        got &= ~1;
        if (got < block.Length) _sourceDone = true;
        if (got == 0)
        {
            _free.Enqueue(block);
            return;
        }

        double left = 0, right = 0;
        float peak = 0;
        for (int i = 0; i < got; i += 2)
        {
            float l = block[i], rr = block[i + 1];
            peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(rr)));
            double kl = _highPass[0].Process(_shelf[0].Process(l));
            double kr = _highPass[1].Process(_shelf[1].Process(rr));
            left += kl * kl;
            right += kr * kr;
        }
        int frames = got / 2;
        // El canal més fort, com si sonés pels dos (una pista amb un canal buit no es puja de més).
        _energy.Add(2 * Math.Max(left, right) / frames);
        _peak.Add(peak);
        _lengths.Add(got);
        _samples.Enqueue(block);
    }
}
