namespace OnaBages.Audio;

/// <summary>Sonoritat d'una pista: LUFS integrats (null si és silenci) i pic de mostra (0..1 o més).</summary>
public sealed record TrackLoudness(double? Lufs, float Peak)
{
    /// <summary>
    /// Mesura un fitxer sencer (el descodifica, fora del fil d'àudio). Les pistes mono es mesuren com
    /// sonen: el mateix senyal pels dos canals.
    /// </summary>
    /// <exception cref="AudioFileException">Si no es pot llegir.</exception>
    public static TrackLoudness Measure(string path, CancellationToken cancel = default)
    {
        const int rate = 48000;
        using var source = AudioFileSource.Open(path, rate);
        var meter = new LoudnessMeter(rate);
        var buffer = new float[rate / 2 * 2]; // 0,5 s
        int read;
        while ((read = source.Read(buffer)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            meter.Add(buffer.AsSpan(0, read));
        }
        return new TrackLoudness(meter.IntegratedLufs is { } lufs ? Math.Round(lufs, 2) : null, meter.SamplePeak);
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

    /// <summary>Filtre de segon ordre (forma directa II transposada), en doble precisió.</summary>
    private sealed class Biquad
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
