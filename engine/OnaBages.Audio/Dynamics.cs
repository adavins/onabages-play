namespace OnaBages.Audio;

/// <summary>
/// Paràmetres del compressor/limitador d'un reproductor (fase 4, pas 4a; D-066).
/// Són els mateixos que el plugin SqrSoft Compressor/Limiter del Winamp, més el sostre de seguretat.
/// </summary>
/// <param name="Enabled">Desactivat, el so passa exactament igual (sense retard ni sostre).</param>
/// <param name="InputGainDb">Guany abans del compressor («Gain» del SqrSoft).</param>
/// <param name="ThresholdDb">Llindar: a partir d'aquí comprimeix («Threshold»).</param>
/// <param name="Ratio">Relació de compressió. 100 = pràcticament un limitador («Ratio»).</param>
/// <param name="AttackMs">Temps d'atac: com de de pressa abaixa («AttackTime»).</param>
/// <param name="ReleaseMs">Temps d'alliberament: com de a poc a poc torna a pujar («ReleaseTime»).</param>
/// <param name="OutputGainDb">Guany després del compressor («OutputGain»).</param>
/// <param name="StereoLink">Els dos canals es comprimeixen igual («StereoTie»). Separats, cada canal va pel seu compte.</param>
/// <param name="CeilingDb">Sostre de seguretat: la sortida no el passa mai (limitador amb anticipació).</param>
public sealed record DynamicsSettings(
    bool Enabled = false,
    double InputGainDb = 0,
    double ThresholdDb = -3,
    double Ratio = 4,
    double AttackMs = 5,
    double ReleaseMs = 500,
    double OutputGainDb = 0,
    bool StereoLink = false,
    double CeilingDb = -0.3)
{
    /// <summary>La configuració del SqrSoft que es fa servir a «La Central» (petició del desenvolupador).</summary>
    public static DynamicsSettings SqrSoftLaCentral { get; } =
        new(true, InputGainDb: 9.5, ThresholdDb: -2.9, Ratio: 100, AttackMs: 4.2, ReleaseMs: 1450, OutputGainDb: 0, StereoLink: false);

    // Marges que s'accepten (fora d'aquí, el valor es porta al límit).
    public const double MinGainDb = -20, MaxGainDb = 30;
    public const double MinThresholdDb = -40, MaxThresholdDb = 0;
    public const double MinRatio = 1, MaxRatio = 100;
    public const double MinAttackMs = 0.1, MaxAttackMs = 200;
    public const double MinReleaseMs = 10, MaxReleaseMs = 5000;
    public const double MinOutputGainDb = -20, MaxOutputGainDb = 20;
    public const double MinCeilingDb = -6, MaxCeilingDb = 0;

    /// <summary>Els mateixos paràmetres, amb cada valor dins del seu marge.</summary>
    public DynamicsSettings Clamped() => this with
    {
        InputGainDb = Clamp(InputGainDb, MinGainDb, MaxGainDb),
        ThresholdDb = Clamp(ThresholdDb, MinThresholdDb, MaxThresholdDb),
        Ratio = Clamp(Ratio, MinRatio, MaxRatio),
        AttackMs = Clamp(AttackMs, MinAttackMs, MaxAttackMs),
        ReleaseMs = Clamp(ReleaseMs, MinReleaseMs, MaxReleaseMs),
        OutputGainDb = Clamp(OutputGainDb, MinOutputGainDb, MaxOutputGainDb),
        CeilingDb = Clamp(CeilingDb, MinCeilingDb, MaxCeilingDb),
    };

    private static double Clamp(double value, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : min;
}

/// <summary>
/// Compressor/limitador per a mostres estèreo intercalades (fase 4, pas 4a; D-066). Dues etapes:
///
/// 1. <b>Compressor</b> com el del SqrSoft: guany d'entrada; per a cada mostra, si el nivell passa del
///    llindar, s'abaixa (amb la ràtio); l'abaixada arriba amb el temps d'atac i se'n va amb el
///    d'alliberament; després, guany de sortida.
/// 2. <b>Sostre</b>: limitador que mira <see cref="LookaheadMs"/> endavant. Si una mostra passaria del
///    sostre, el guany ja ha baixat (de manera suau) quan hi arriba. Garanteix que la sortida no passa
///    mai del sostre, encara que un cop sigui massa ràpid per a l'atac del compressor.
///
/// <see cref="Process"/> s'executa al fil d'àudio: no crea objectes, no bloqueja i no escriu registres.
/// Els paràmetres es canvien des d'un altre fil (<see cref="Settings"/>); els guanys d'entrada i de
/// sortida hi arriben de mica en mica (uns 20 ms), perquè canviar-los (o activar-lo) no faci clics.
/// Activat, el sostre retarda el so <see cref="LookaheadMs"/>. Desactivat, el so passa exactament igual, sense
/// retard; en activar-lo o desactivar-lo hi ha una transició de 10 ms entre els dos, perquè el salt no faci clic.
/// </summary>
public sealed class DynamicsProcessor
{
    /// <summary>Quant mira endavant el sostre (i quant es retarda el so).</summary>
    public const double LookaheadMs = 1.5;

    /// <summary>Temps en què els guanys d'entrada i de sortida arriben al valor nou.</summary>
    private const double GainSmoothingMs = 20;

    /// <summary>Alliberament del sostre: com de de pressa torna a pujar després d'un cop.</summary>
    private const double CeilingReleaseMs = 60;

    /// <summary>Transició entre el so sense processar i el processat, en activar-lo o desactivar-lo.</summary>
    private const double CrossfadeMs = 10;

    private readonly int _sampleRate;

    // --- Paràmetres (els prepara Settings, en el fil que crida; el fil d'àudio només en llegeix la referència) ---
    private volatile Coefficients _coefficients;
    private DynamicsSettings _settings;

    // --- Estat del fil d'àudio ---
    private float _inputGain = 1f, _outputGain = 1f;     // guanys suavitzats (lineals)
    private float _reductionLeft, _reductionRight;       // abaixada del compressor (dB, ≥ 0)
    private readonly float[] _delayLeft, _delayRight;    // so retardat (anticipació del sostre)
    private readonly float[] _required;                  // guany que necessitaria cada mostra per no passar del sostre
    private readonly float[] _hold;                      // mínim d'aquests guanys en la finestra d'anticipació
    private int _position;                                // posició circular de les memòries
    private double _holdSum;                              // suma de _hold (per a la mitjana mòbil)
    private float _ceilingGain = 1f;                      // guany del sostre, amb alliberament
    private readonly int _lookahead;                      // mostres per canal d'anticipació
    private int _samplesSinceResum;
    private float _wet;                                   // 0 = so sense processar, 1 = processat (transició)
    private readonly float _wetStep;                      // quant avança la transició per mostra

    /// <summary>Abaixada màxima (compressor + sostre) des de l'última lectura, en centèsimes de dB.</summary>
    private int _maxReductionCentiDb;

    /// <param name="sampleRate">Freqüència de les mostres (la de la sortida).</param>
    /// <param name="settings">Paràmetres inicials (per defecte, desactivat).</param>
    public DynamicsProcessor(int sampleRate, DynamicsSettings? settings = null)
    {
        _sampleRate = sampleRate;
        _lookahead = Math.Max(1, (int)Math.Round(sampleRate * LookaheadMs / 1000));
        _delayLeft = new float[_lookahead];
        _delayRight = new float[_lookahead];
        _required = new float[_lookahead];
        _hold = new float[_lookahead];
        Array.Fill(_required, 1f);
        Array.Fill(_hold, 1f);
        _holdSum = _lookahead;

        _wetStep = (float)(1000.0 / (CrossfadeMs * sampleRate));

        _settings = (settings ?? new DynamicsSettings()).Clamped();
        _coefficients = Coefficients.From(_settings, sampleRate);
        // Al principi, els guanys ja són els definitius (no cal arribar-hi a poc a poc).
        _inputGain = _coefficients.InputGain;
        _outputGain = _coefficients.OutputGain;
        _wet = _settings.Enabled ? 1f : 0f;
    }

    /// <summary>Paràmetres actuals. Es poden canviar en qualsevol moment, des de qualsevol fil.</summary>
    public DynamicsSettings Settings
    {
        get => _settings;
        set
        {
            _settings = value.Clamped();
            _coefficients = Coefficients.From(_settings, _sampleRate);
        }
    }

    /// <summary>
    /// Abaixada màxima de guany (compressor + sostre) des de l'última crida, en dB (≥ 0), i la posa a zero.
    /// Per a l'indicador de reducció (unes 25 vegades per segon).
    /// </summary>
    public float TakeMaxReductionDb() => Interlocked.Exchange(ref _maxReductionCentiDb, 0) / 100f;

    /// <summary>
    /// Quantes mostres per canal queden retingudes dins seu (l'anticipació del sostre): 0 si està desactivat.
    /// En acabar un enregistrament, s'hi passen aquestes mostres de silenci perquè surti tot el so. Del mateix fil que <see cref="Process"/>.
    /// </summary>
    public int LatencyFrames => _wet > 0f ? _lookahead - 1 : 0;

    /// <summary>
    /// Processa mostres estèreo intercalades (E, D, E, D...), al mateix lloc. Fil d'àudio.
    /// </summary>
    public void Process(Span<float> stereo)
    {
        var c = _coefficients; // una sola lectura per bloc: tot el bloc fa servir els mateixos paràmetres
        float maxReductionDb = 0;
        int frames = stereo.Length / 2;

        // Desactivat (i la transició ja acabada): el so passa exactament igual, sense retard.
        // Només es va guardant a la memòria d'anticipació, per tenir-la plena si s'activa.
        if (!c.Enabled && _wet <= 0f)
        {
            for (int f = 0; f < frames; f++)
            {
                _delayLeft[_position] = stereo[f * 2];
                _delayRight[_position] = stereo[f * 2 + 1];
                _position = (_position + 1) % _lookahead;
            }
            return;
        }

        float wetTarget = c.Enabled ? 1f : 0f;
        for (int f = 0; f < frames; f++)
        {
            int i = f * 2;
            float dryLeft = stereo[i], dryRight = stereo[i + 1];
            _wet = wetTarget > _wet ? Math.Min(1f, _wet + _wetStep) : Math.Max(0f, _wet - _wetStep);

            // Guanys d'entrada i de sortida: cap al valor nou, de mica en mica.
            _inputGain += (c.InputGain - _inputGain) * c.GainSmoothing;
            _outputGain += (c.OutputGain - _outputGain) * c.GainSmoothing;

            float left = stereo[i] * _inputGain;
            float right = stereo[i + 1] * _inputGain;

            // --- 1. Compressor ---
            float levelLeft = Math.Abs(left), levelRight = Math.Abs(right);
            if (c.StereoLink)
            {
                float level = Math.Max(levelLeft, levelRight);
                levelLeft = levelRight = level;
            }
            _reductionLeft = Follow(_reductionLeft, TargetReductionDb(levelLeft, c), c);
            _reductionRight = c.StereoLink ? _reductionLeft : Follow(_reductionRight, TargetReductionDb(levelRight, c), c);

            left *= DbToGain(-_reductionLeft) * _outputGain;
            right *= DbToGain(-_reductionRight) * _outputGain;

            // --- 2. Sostre amb anticipació ---
            // Guany que necessita aquesta mostra (la que entra ara) per no passar del sostre.
            float peak = Math.Max(Math.Abs(left), Math.Abs(right));
            float required = peak > c.Ceiling ? c.Ceiling / peak : 1f;

            // Mínim dels guanys necessaris de les últimes _lookahead mostres (inclosa aquesta).
            _required[_position] = required;
            float hold = 1f;
            for (int k = 0; k < _lookahead; k++)
            {
                if (_required[k] < hold) hold = _required[k];
            }

            // Mitjana mòbil d'aquests mínims: el guany baixa de manera suau i, com que cada mínim ja
            // inclou la mostra que surt ara (retardada _lookahead - 1), mai no queda per sobre del necessari.
            _holdSum += hold - _hold[_position];
            _hold[_position] = hold;
            float smooth = (float)(_holdSum / _lookahead);
            // Cada cert temps es torna a sumar de nou, perquè els errors d'arrodoniment no s'acumulin.
            if (++_samplesSinceResum >= 4096)
            {
                _samplesSinceResum = 0;
                double sum = 0;
                for (int k = 0; k < _lookahead; k++) sum += _hold[k];
                _holdSum = sum;
            }

            // Baixa de seguida (ja ve suau); torna a pujar amb l'alliberament.
            _ceilingGain = smooth < _ceilingGain
                ? smooth
                : _ceilingGain + (smooth - _ceilingGain) * c.CeilingRelease;

            // La mostra que surt és la que va entrar fa _lookahead - 1 posicions.
            int oldest = (_position + 1) % _lookahead;
            float outLeft = _delayLeft[oldest] * _ceilingGain;
            float outRight = _delayRight[oldest] * _ceilingGain;
            _delayLeft[_position] = left;
            _delayRight[_position] = right;
            _position = oldest;

            // Per si de cas (arrodoniments): mai per sobre del sostre.
            outLeft = Math.Clamp(outLeft, -c.Ceiling, c.Ceiling);
            outRight = Math.Clamp(outRight, -c.Ceiling, c.Ceiling);

            // Normalment, només el processat. Durant la transició (10 ms), una barreja amb el so sense processar.
            if (_wet >= 1f)
            {
                stereo[i] = outLeft;
                stereo[i + 1] = outRight;
            }
            else
            {
                stereo[i] = dryLeft + (outLeft - dryLeft) * _wet;
                stereo[i + 1] = dryRight + (outRight - dryRight) * _wet;
            }

            float reduction = (Math.Max(_reductionLeft, _reductionRight) - GainToDb(_ceilingGain)) * _wet;
            if (reduction > maxReductionDb) maxReductionDb = reduction;
        }

        // S'acaba de desactivar del tot: l'estat torna a zero, perquè en tornar-lo a activar comenci net.
        if (!c.Enabled && _wet <= 0f)
            ResetDynamicsState();

        // Indicador de reducció: el màxim des de l'última lectura.
        int centi = (int)(maxReductionDb * 100);
        int current;
        while (centi > (current = Volatile.Read(ref _maxReductionCentiDb))
               && Interlocked.CompareExchange(ref _maxReductionCentiDb, centi, current) != current)
        {
        }
    }

    private void ResetDynamicsState()
    {
        _reductionLeft = _reductionRight = 0;
        _ceilingGain = 1f;
        Array.Fill(_required, 1f);
        Array.Fill(_hold, 1f);
        _holdSum = _lookahead;
        _inputGain = _coefficients.InputGain;
        _outputGain = _coefficients.OutputGain;
    }

    /// <summary>Quant s'ha d'abaixar (dB) una mostra d'aquest nivell: el que passa del llindar, per (1 − 1/ràtio).</summary>
    private static float TargetReductionDb(float level, Coefficients c)
    {
        if (level <= c.ThresholdLinear || c.Slope <= 0) return 0;
        return (GainToDb(level) - c.ThresholdDb) * c.Slope;
    }

    /// <summary>L'abaixada va cap a l'objectiu: de pressa si ha de baixar més (atac), a poc a poc si no (alliberament).</summary>
    private static float Follow(float current, float target, Coefficients c) =>
        target > current
            ? current + (target - current) * c.Attack
            : current + (target - current) * c.Release;

    private static float DbToGain(float db) => MathF.Exp(db * 0.115129255f); // 10^(dB/20)

    private static float GainToDb(float gain) => 20f * MathF.Log10(Math.Max(gain, 1e-9f));

    /// <summary>Paràmetres convertits per al fil d'àudio (es calculen una vegada en canviar-los).</summary>
    private sealed class Coefficients
    {
        public float InputGain, OutputGain, ThresholdDb, ThresholdLinear, Slope, Attack, Release, Ceiling, CeilingRelease, GainSmoothing;
        public bool StereoLink, Enabled;

        public static Coefficients From(DynamicsSettings s, int sampleRate)
        {
            // Desactivat: guanys a 0 dB i sense compressió (és cap on va el so durant la transició de desactivar-lo).
            bool on = s.Enabled;
            return new Coefficients
            {
                InputGain = on ? (float)Math.Pow(10, s.InputGainDb / 20) : 1f,
                OutputGain = on ? (float)Math.Pow(10, s.OutputGainDb / 20) : 1f,
                ThresholdDb = (float)s.ThresholdDb,
                ThresholdLinear = (float)Math.Pow(10, s.ThresholdDb / 20),
                Slope = on ? (float)(1 - 1 / s.Ratio) : 0f,
                Attack = SmoothingFactor(s.AttackMs, sampleRate),
                Release = SmoothingFactor(s.ReleaseMs, sampleRate),
                Ceiling = on ? (float)Math.Pow(10, s.CeilingDb / 20) : 1f,
                CeilingRelease = SmoothingFactor(CeilingReleaseMs, sampleRate),
                GainSmoothing = SmoothingFactor(GainSmoothingMs, sampleRate),
                StereoLink = s.StereoLink,
                Enabled = on,
            };
        }

        /// <summary>Factor d'un filtre d'un pol: en <paramref name="ms"/> mil·lisegons fa el 63 % del camí.</summary>
        private static float SmoothingFactor(double ms, int sampleRate) =>
            (float)(1 - Math.Exp(-1000.0 / (ms * sampleRate)));
    }
}
