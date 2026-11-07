namespace OnaBages.Audio;

/// <summary>
/// On hi ha so en una pista per a un llindar (fase 5; D-079): de la primera mostra que el passa
/// (<paramref name="Start"/>, en segons) fins al final de l'última (<paramref name="End"/>).
/// </summary>
public readonly record struct SoundBounds(int ThresholdDb, double Start, double End);

/// <summary>
/// Canal sense so d'un fitxer estèreo (fase 5, pas 29a; D-132): el fitxer és estèreo però només sona per un canal.
/// </summary>
public enum EmptyChannel { None, Left, Right }

/// <summary>
/// Anàlisi d'una pista: sonoritat (LUFS integrats, null si és silenci), pic de mostra (0..1 o més) i
/// on comença i on acaba el so per a cada llindar de <see cref="TrackLoudness.SilenceThresholdsDb"/>
/// (per al retall de silencis; un llindar sense cap mostra per sobre no hi és).
/// </summary>
public sealed record TrackLoudness(double? Lufs, float Peak, IReadOnlyList<SoundBounds>? Sound = null,
    EmptyChannel EmptyChannel = EmptyChannel.None)
{
    /// <summary>Un canal és buit si el seu pic no passa d'aquí (dBFS)...</summary>
    public const double EmptyChannelMaxDb = -50;

    /// <summary>... i l'altre és, com a mínim, tants dB més fort (el soroll d'un canal obert no compta com a buit).</summary>
    public const double EmptyChannelGapDb = 30;

    /// <summary>I l'altre ha de tenir so de debò (pic per sobre d'aquí): un fitxer gairebé mut no té «un canal buit».</summary>
    public const double EmptyChannelOtherMinDb = -40;

    /// <summary>Quin canal és buit, a partir del pic de cada un (0..1). Si tots dos són silenci, cap.</summary>
    public static EmptyChannel FindEmptyChannel(float leftPeak, float rightPeak)
    {
        double left = 20 * Math.Log10(Math.Max(leftPeak, 1e-10f));
        double right = 20 * Math.Log10(Math.Max(rightPeak, 1e-10f));
        if (left <= EmptyChannelMaxDb && right >= EmptyChannelOtherMinDb && right - left >= EmptyChannelGapDb) return EmptyChannel.Left;
        if (right <= EmptyChannelMaxDb && left >= EmptyChannelOtherMinDb && left - right >= EmptyChannelGapDb) return EmptyChannel.Right;
        return EmptyChannel.None;
    }

    /// <summary>
    /// Llindars de silenci que es poden triar (dBFS de pic). Es mesuren tots alhora: si es canvia el
    /// llindar, no cal tornar a analitzar les pistes.
    /// </summary>
    public static readonly int[] SilenceThresholdsDb = [-40, -50, -60, -70, -80];

    /// <summary>On hi ha so per a un llindar, o null (silenci, o no s'ha analitzat).</summary>
    public SoundBounds? SoundFor(int thresholdDb)
    {
        if (Sound is null) return null;
        foreach (var b in Sound)
        {
            if (b.ThresholdDb == thresholdDb) return b;
        }
        return null;
    }

    /// <summary>
    /// Analitza un fitxer sencer (el descodifica una vegada, fora del fil d'àudio). Les pistes mono es
    /// mesuren com sonen: el mateix senyal pels dos canals. El so es busca a la freqüència del fitxer
    /// (sense remostrejar), amb precisió d'una mostra: compta com a so qualsevol mostra, de qualsevol
    /// canal, amb un pic per sobre del llindar.
    /// </summary>
    /// <exception cref="AudioFileException">Si no es pot llegir.</exception>
    public static TrackLoudness Measure(string path, CancellationToken cancel = default)
    {
        using var source = AudioFileSource.Open(path, 0); // la freqüència del fitxer
        int rate = source.SampleRate;
        var meter = new LoudnessMeter(rate);

        var thresholds = SilenceThresholdsDb.Select(db => (float)Math.Pow(10, db / 20.0)).ToArray();
        var first = Enumerable.Repeat(-1L, thresholds.Length).ToArray();
        var last = new long[thresholds.Length];
        var edges = new SoundEdges[thresholds.Length];
        for (int k = 0; k < edges.Length; k++) edges[k] = new SoundEdges(rate);
        long frame = 0;
        float leftPeak = 0, rightPeak = 0; // per saber si un canal és buit (pas 29a)

        var buffer = new float[rate / 2 * 2]; // 0,5 s
        int read;
        while ((read = source.Read(buffer)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            meter.Add(buffer.AsSpan(0, read));
            for (int i = 0; i + 1 < read; i += 2, frame++)
            {
                float l = Math.Abs(buffer[i]), r = Math.Abs(buffer[i + 1]);
                if (l > leftPeak) leftPeak = l;
                if (r > rightPeak) rightPeak = r;
                float peak = Math.Max(l, r);
                // Del llindar més baix (−80) al més alt (−40): si no passa d'un, tampoc dels de més amunt.
                for (int k = thresholds.Length - 1; k >= 0; k--)
                {
                    if (peak <= thresholds[k]) break;
                    if (first[k] < 0) first[k] = frame;
                    last[k] = frame;
                    edges[k].Add(frame);
                }
            }
        }

        // Els llindars que no tenen cap mostra per sobre (silenci) no es posen. Si només hi ha espetecs
        // aïllats, es fan servir tal com són (no es retalla tota la pista).
        var sound = new List<SoundBounds>();
        for (int k = 0; k < thresholds.Length; k++)
        {
            if (first[k] < 0) continue;
            var (start, end) = edges[k].Finish() ?? (first[k], last[k]);
            sound.Add(new SoundBounds(SilenceThresholdsDb[k], (double)start / rate, (double)(end + 1) / rate));
        }
        return new TrackLoudness(meter.IntegratedLufs is { } lufs ? Math.Round(lufs, 2) : null, meter.SamplePeak, sound,
            FindEmptyChannel(leftPeak, rightPeak));
    }

    /// <summary>Un tros de so més curt que això, sol entre silencis, és un espetec i no compta (ms).</summary>
    public const int ClickMaxMs = 20;

    /// <summary>Silenci mínim abans i després d'un espetec perquè es consideri aïllat (ms).</summary>
    public const int ClickGapMs = 500;

    /// <summary>
    /// On comença i on acaba el so d'un llindar sense comptar els espetecs aïllats (D-082): p. ex. un
    /// fitxer amb la veu fins als 203 s, 14 s de silenci digital i un espetec de 4 ms al final (un
    /// defecte de la codificació). Es reben, en ordre, les mostres que passen el llindar; es van agrupant
    /// en trossos (separats per <see cref="ClickGapMs"/> de silenci com a mínim) i els trossos de menys
    /// de <see cref="ClickMaxMs"/> no compten. Un so de debò (fins i tot un cop de percussió) dura molt
    /// més per sobre del llindar, de manera que no es pot perdre so.
    /// </summary>
    private sealed class SoundEdges(int rate)
    {
        private readonly long _gap = (long)ClickGapMs * rate / 1000;
        private readonly long _minLength = (long)ClickMaxMs * rate / 1000;
        private long _pieceStart = -1, _pieceEnd = -1; // el tros d'ara
        private long _start = -1, _end = -1;           // del primer al darrer tros que compta

        public void Add(long frame)
        {
            if (_pieceStart >= 0 && frame - _pieceEnd > _gap) Close();
            if (_pieceStart < 0) _pieceStart = frame;
            _pieceEnd = frame;
        }

        /// <summary>On comença i on acaba el so (mostres), o null si només hi ha espetecs.</summary>
        public (long Start, long End)? Finish()
        {
            Close();
            return _start >= 0 ? (_start, _end) : null;
        }

        private void Close()
        {
            if (_pieceStart < 0) return;
            if (_pieceEnd - _pieceStart + 1 >= _minLength)
            {
                if (_start < 0) _start = _pieceStart;
                _end = _pieceEnd;
            }
            _pieceStart = _pieceEnd = -1;
        }
    }
}

/// <summary>
/// Memòria cau de la sonoritat de les pistes (la implementa el mòdul de dades del motor):
/// així una pista que ja s'ha fet servir no s'ha de tornar a mesurar.
/// </summary>
public interface ILoudnessCache
{
    /// <summary>La sonoritat desada d'aquest fitxer (tal com és ara), o null.</summary>
    TrackLoudness? Read(string path);

    void Write(string path, TrackLoudness loudness);
}

/// <summary>
/// Mesurador de sonoritat integrada (LUFS) segons la recomanació ITU-R BS.1770-4 i l'EBU R128
/// (fase 4, pas 5a; D-070). És el que fan servir les ràdios i les televisions per saber com de fort
/// <i>sona</i> una pista (no només quins pics té): així es poden igualar les pistes entre elles.
///
/// Com es calcula:
/// 1. Filtre «K» a cada canal: realça els aguts (com l'oïda) i treu els greus molt greus.
/// 2. Energia mitjana en blocs de 400 ms que se solapen un 75 % (un de nou cada 100 ms).
/// 3. Es descarten els blocs gairebé en silenci (per sota de −70 LUFS) i, després, els que són
///    més de 10 LU per sota de la mitjana dels que queden (les pauses i les parts molt fluixes).
/// 4. La sonoritat és la mitjana dels blocs que queden.
///
/// També guarda el pic de mostra més alt. Es fa servir fora del fil d'àudio (en analitzar les pistes).
/// </summary>
public sealed class LoudnessMeter
{
    private const double AbsoluteGateLufs = -70;
    private const double RelativeGateLu = -10;

    private readonly int _channels;
    private readonly Biquad[] _shelf;
    private readonly Biquad[] _highPass;
    private readonly int _subBlockFrames;               // 100 ms
    private readonly double[] _subBlockEnergy = new double[4]; // energia dels últims 4 sub-blocs (= 400 ms)
    private int _subBlocksFilled;
    private int _subBlockIndex;
    private double _currentEnergy;                      // suma de quadrats ponderats del sub-bloc en curs
    private int _currentFrames;
    private readonly List<double> _blocks = [];         // energia mitjana de cada bloc de 400 ms
    private float _peak;

    /// <param name="sampleRate">Freqüència de les mostres.</param>
    /// <param name="channels">1 o 2 (els dos canals d'una pista estèreo pesen igual).</param>
    public LoudnessMeter(int sampleRate, int channels = 2)
    {
        if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));
        _channels = channels;
        _subBlockFrames = sampleRate / 10;
        _shelf = new Biquad[channels];
        _highPass = new Biquad[channels];
        for (int c = 0; c < channels; c++)
        {
            _shelf[c] = Biquad.HighShelf(sampleRate, 1681.974450955533, 3.999843853973347, 0.7071752369554196);
            _highPass[c] = Biquad.HighPass(sampleRate, 38.13547087602444, 0.5003270373238773);
        }
    }

    /// <summary>Afegeix mostres intercalades (amb els canals del constructor).</summary>
    public void Add(ReadOnlySpan<float> samples)
    {
        for (int i = 0; i + _channels <= samples.Length; i += _channels)
        {
            double energy = 0;
            for (int c = 0; c < _channels; c++)
            {
                float x = samples[i + c];
                float abs = Math.Abs(x);
                if (abs > _peak) _peak = abs;
                double k = _highPass[c].Process(_shelf[c].Process(x));
                energy += k * k; // els canals esquerre i dret pesen 1,0
            }
            _currentEnergy += energy;
            if (++_currentFrames == _subBlockFrames)
                CloseSubBlock();
        }
    }

    private void CloseSubBlock()
    {
        _subBlockEnergy[_subBlockIndex] = _currentEnergy;
        _subBlockIndex = (_subBlockIndex + 1) % 4;
        _currentEnergy = 0;
        _currentFrames = 0;
        if (_subBlocksFilled < 4) _subBlocksFilled++;
        if (_subBlocksFilled == 4)
        {
            // Bloc de 400 ms: els últims 4 sub-blocs de 100 ms.
            double sum = _subBlockEnergy[0] + _subBlockEnergy[1] + _subBlockEnergy[2] + _subBlockEnergy[3];
            _blocks.Add(sum / (4.0 * _subBlockFrames));
        }
    }

    /// <summary>
    /// Sonoritat integrada en LUFS, o null si no n'hi ha prou (menys de 400 ms) o tot és gairebé silenci.
    /// </summary>
    public double? IntegratedLufs
    {
        get
        {
            double absoluteGate = EnergyFor(AbsoluteGateLufs);
            var loud = _blocks.Where(e => e > absoluteGate).ToList();
            if (loud.Count == 0) return null;

            double relativeGate = loud.Average() * Math.Pow(10, RelativeGateLu / 10);
            var gated = loud.Where(e => e > relativeGate).ToList();
            return gated.Count == 0 ? null : LufsFor(gated.Average());
        }
    }

    /// <summary>Pic de mostra més alt (0..1 o més), en valor absolut.</summary>
    public float SamplePeak => _peak;

    private static double LufsFor(double energy) => -0.691 + 10 * Math.Log10(energy);

    private static double EnergyFor(double lufs) => Math.Pow(10, (lufs + 0.691) / 10);

    /// <summary>Filtre de segon ordre (forma directa II transposada), en doble precisió. També el fa servir l'anivellador (D-134).</summary>
    internal sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _z1, _z2;

        private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0;
        }

        public double Process(double x)
        {
            double y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }

        /// <summary>Prestatge d'aguts (primera etapa del filtre K), recalculat per a qualsevol freqüència.</summary>
        public static Biquad HighShelf(int sampleRate, double f0, double gainDb, double q)
        {
            double k = Math.Tan(Math.PI * f0 / sampleRate);
            double vh = Math.Pow(10, gainDb / 20);
            double vb = Math.Pow(vh, 0.4996667741545416);
            double a0 = 1 + k / q + k * k;
            return new Biquad(
                vh + vb * k / q + k * k,
                2 * (k * k - vh),
                vh - vb * k / q + k * k,
                a0,
                2 * (k * k - 1),
                1 - k / q + k * k);
        }

        /// <summary>
        /// Passaaltes (segona etapa del filtre K). Com a la recomanació, el numerador és {1, −2, 1} sense
        /// normalitzar (a 48 kHz dona exactament els coeficients de la taula de BS.1770).
        /// </summary>
        public static Biquad HighPass(int sampleRate, double f0, double q)
        {
            double k = Math.Tan(Math.PI * f0 / sampleRate);
            double a0 = 1 + k / q + k * k;
            return new Biquad(1, -2, 1, 1, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
        }
    }
}
