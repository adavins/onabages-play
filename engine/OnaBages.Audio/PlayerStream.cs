using System.Runtime.InteropServices;
using NAudio.Wave;

namespace OnaBages.Audio;

/// <summary>
/// El tros de reproductor que corre al <b>fil d'àudio</b> de Windows.
/// Només copia mostres de la memòria intermèdia cap a la sortida i compta.
/// No llegeix el disc, no crea objectes, no espera i no escriu registres.
///
/// Si no hi ha prou dades, envia silenci:
/// - si el fitxer ja s'ha acabat de llegir, marca <see cref="EndReached"/>;
/// - si no, és un tall (el disc no ha anat prou ràpid) i es compta a <see cref="Underruns"/>.
/// </summary>
public sealed class PlayerStream : IWaveProvider
{
    private SampleRingBuffer _ring;
    private readonly int _outChannels;
    private readonly float[] _stereo; // només per a sortides de més de 2 canals

    // --- Canvi de pista a mà amb fos (fase 5; D-078) ---
    // El reproductor deixa preparada la memòria de la pista nova (_pendingRing); el fil d'àudio hi passa
    // en el bloc següent i, durant el fos, també llegeix de la de la pista que s'acaba (_outgoing).
    private SampleRingBuffer? _pendingRing;
    private int _pendingFadeFrames;
    private SampleRingBuffer? _outgoing; // només el fa servir el fil d'àudio
    private int _fadeTotal, _fadeDone;    // idem
    private readonly float[] _scratch;    // idem: la pista que s'acaba, abans de barrejar-la

    private long _framesPlayed;
    private int _underruns;
    private volatile bool _sourceFinished;
    private volatile bool _endReached;
    private long _framesAfterEnd;
    private readonly long _drainFrames;
    private float _currentGain; // només el fa servir el fil d'àudio
    private volatile float _gain;

    /// <summary>Guany que es vol (1 = sense canvi, 0 = silenci). El fixa el reproductor quan es mou el fader.</summary>
    public float Gain
    {
        get => _gain;
        set => _gain = value;
    }

    /// <summary>Pic del que surt (després del volum), per al mesurador.</summary>
    public PeakMeter Meter { get; } = new();

    /// <summary>
    /// Compressor/limitador (fase 4, pas 4b; D-067): s'aplica a l'estèreo abans del volum, com un DSP del Winamp.
    /// Els seus paràmetres es poden canviar mentre sona (<see cref="DynamicsProcessor.Settings"/>).
    /// </summary>
    public DynamicsProcessor Dynamics { get; }

    /// <param name="ring">Memòria amb mostres estèreo intercalades.</param>
    /// <param name="sampleRate">Freqüència de la sortida.</param>
    /// <param name="outChannels">Canals de la sortida. L'estèreo va als dos primers; la resta, silenci.</param>
    /// <param name="gain">Guany inicial (el volum del reproductor).</param>
    /// <param name="dynamics">Compressor/limitador (per defecte, desactivat).</param>
    public PlayerStream(SampleRingBuffer ring, int sampleRate, int outChannels, float gain = 1f, DynamicsSettings? dynamics = null)
    {
        Dynamics = new DynamicsProcessor(sampleRate, dynamics);
        _ring = ring;
        _outChannels = outChannels;
        _drainFrames = sampleRate * DrainMilliseconds / 1000;
        _gain = gain;
        _currentGain = gain; // sense rampa al principi
        _stereo = outChannels > 2 ? new float[sampleRate * 2] : [];
        _scratch = new float[sampleRate / 10 * 2]; // 100 ms; si un bloc és més llarg, es barreja a trossos
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, outChannels);
    }

    /// <summary>
    /// Pista nova a mà (fase 5; D-078), abans d'arrencar el seu fil de lectura: es tornen a zero els avisos de final
    /// (el fil de lectura nou els tornarà a marcar quan toqui). Fil de les ordres.
    /// </summary>
    public void PrepareForNewTrack()
    {
        _sourceFinished = false;
        _endReached = false;
        Interlocked.Exchange(ref _framesAfterEnd, 0);
    }

    /// <summary>
    /// Passa a la memòria de la pista nova en el bloc següent, amb un fos de <paramref name="fadeFrames"/>
    /// mostres per canal: la pista que sonava baixa i la nova puja (corbes d'igual potència). La memòria
    /// de la que sonava ja no rep més dades (el seu fil de lectura s'ha aturat): se'n fan servir les que hi ha.
    /// <see cref="FramesPlayed"/> torna a començar de zero. Fil de les ordres.
    /// </summary>
    public void SwitchTo(SampleRingBuffer ring, int fadeFrames)
    {
        _pendingFadeFrames = Math.Max(1, fadeFrames);
        Volatile.Write(ref _pendingRing, ring);
    }

    /// <summary>Hi ha un canvi de pista preparat que el fil d'àudio encara no ha fet.</summary>
    public bool SwitchPending => Volatile.Read(ref _pendingRing) is not null;

    /// <summary>
    /// Fil d'àudio: si hi ha una pista nova preparada, hi passa. Sense crear objectes: només canvia referències.
    /// </summary>
    private void TakePendingSwitch()
    {
        var pending = Interlocked.Exchange(ref _pendingRing, null);
        if (pending is null) return;
        _outgoing = _ring;
        _ring = pending;
        _fadeTotal = _pendingFadeFrames;
        _fadeDone = 0;
        Interlocked.Exchange(ref _framesPlayed, 0);
    }

    /// <summary>
    /// Fil d'àudio: barreja la pista que s'acaba amb la nova (que ja és a <paramref name="stereo"/>).
    /// Quan s'acaba el fos, la memòria de la que s'acaba es deixa anar.
    /// </summary>
    private void MixOutgoing(Span<float> stereo, int frames)
    {
        var outgoing = _outgoing;
        if (outgoing is null) return;

        for (int done = 0; done < frames;)
        {
            int piece = Math.Min(frames - done, _scratch.Length / 2);
            int got = outgoing.Read(_scratch.AsSpan(0, piece * 2)) / 2;
            for (int f = 0; f < piece; f++)
            {
                double t = (_fadeDone + done + f + 0.5) / _fadeTotal;
                float fadeIn = t >= 1 ? 1f : (float)Math.Sin(t * Math.PI / 2);
                float fadeOut = t >= 1 ? 0f : (float)Math.Cos(t * Math.PI / 2);
                int i = (done + f) * 2;
                float oldLeft = f < got ? _scratch[f * 2] : 0f;
                float oldRight = f < got ? _scratch[f * 2 + 1] : 0f;
                stereo[i] = stereo[i] * fadeIn + oldLeft * fadeOut;
                stereo[i + 1] = stereo[i + 1] * fadeIn + oldRight * fadeOut;
            }
            done += piece;
        }

        _fadeDone += frames;
        if (_fadeDone >= _fadeTotal) _outgoing = null;
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Mostres per canal enviades a la sortida des del principi.</summary>
    public long FramesPlayed => Interlocked.Read(ref _framesPlayed);

    /// <summary>Vegades que la sortida ha demanat dades i no n'hi havia (talls).</summary>
    public int Underruns => Volatile.Read(ref _underruns);

    /// <summary>
    /// Silenci que s'envia després de l'última mostra abans de donar la pista per acabada.
    /// Quan s'envia l'última mostra, encara és a la memòria de la targeta (fins a uns 100 ms):
    /// si s'aturés la sortida en aquell moment, se'n tallaria el final.
    /// </summary>
    public const int DrainMilliseconds = 200;

    /// <summary>S'ha enviat tot el fitxer i la memòria és buida.</summary>
    public bool EndReached => _endReached;

    /// <summary>
    /// Ha sonat tot: després de l'última mostra s'ha enviat prou silenci perquè hagi sortit de la targeta.
    /// Fins llavors no es pot aturar la sortida.
    /// </summary>
    public bool PlayedOut => _endReached && Interlocked.Read(ref _framesAfterEnd) >= _drainFrames;

    /// <summary>El fil de lectura ja ha llegit tot el fitxer.</summary>
    public bool SourceFinished => _sourceFinished;

    /// <summary>El fil de lectura avisa que ja no hi haurà més dades.</summary>
    public void MarkSourceFinished() => _sourceFinished = true;

    /// <summary>Crida del fil d'àudio. Sempre omple tota la memòria demanada (amb silenci si cal).</summary>
    public int Read(Span<byte> buffer)
    {
        Span<float> output = MemoryMarshal.Cast<byte, float>(buffer);
        int frames = output.Length / _outChannels;
        TakePendingSwitch();

        int framesRead = _outChannels == 2
            ? _ring.Read(output[..(frames * 2)]) / 2
            : ReadIntoWiderOutput(output, frames);

        if (framesRead < frames)
        {
            output[(framesRead * _outChannels)..].Clear();

            // Les dades que hi hagi a la memòria s'escriuen abans de marcar el final:
            // si el fitxer s'ha acabat i la memòria és buida, és el final de debò.
            if (_sourceFinished && _ring.Count == 0)
                _endReached = true;
            else if (!_endReached)
                Interlocked.Increment(ref _underruns);

            if (_endReached)
                Interlocked.Add(ref _framesAfterEnd, frames - framesRead);
        }

        // Canvi de pista a mà amb fos: la pista que s'acaba, a sobre de la nova (a les sortides de més
        // de 2 canals es barreja abans d'eixamplar-ho, a ReadIntoWiderOutput).
        if (_outChannels == 2)
            MixOutgoing(output[..(frames * 2)], frames);

        // Tot el bloc (també el silenci del final): el compressor retarda el so uns 1,5 ms i així no se'n perd la cua.
        if (_outChannels == 2)
            Dynamics.Process(output[..(frames * 2)]);
        ApplyGain(output, frames);
        Meter.AddInterleaved(output[..(frames * _outChannels)], _outChannels);

        Interlocked.Add(ref _framesPlayed, framesRead);
        return buffer.Length;
    }

    /// <summary>
    /// Volum: multiplica els dos primers canals pel guany. Si el guany ha canviat, hi arriba
    /// de mica en mica al llarg del bloc (una rampa), perquè moure el fader no faci clics.
    /// </summary>
    private void ApplyGain(Span<float> output, int frames)
    {
        float target = Gain;
        float current = _currentGain;
        if (current == 1f && target == 1f) return;

        float step = frames > 0 ? (target - current) / frames : 0;
        for (int i = 0; i < frames; i++)
        {
            current += step;
            int o = i * _outChannels;
            output[o] *= current;
            output[o + 1] *= current;
        }
        _currentGain = target;
    }

    private int ReadIntoWiderOutput(Span<float> output, int frames)
    {
        int framesRead = 0;
        while (framesRead < frames)
        {
            int chunk = Math.Min(frames - framesRead, _stereo.Length / 2);
            int got = _ring.Read(_stereo.AsSpan(0, chunk * 2)) / 2;
            MixOutgoing(_stereo.AsSpan(0, got * 2), got);
            Dynamics.Process(_stereo.AsSpan(0, got * 2));
            for (int i = 0; i < got; i++)
            {
                int o = (framesRead + i) * _outChannels;
                output[o] = _stereo[i * 2];
                output[o + 1] = _stereo[i * 2 + 1];
                output.Slice(o + 2, _outChannels - 2).Clear();
            }
            framesRead += got;
            if (got < chunk) break;
        }
        return framesRead;
    }
}
