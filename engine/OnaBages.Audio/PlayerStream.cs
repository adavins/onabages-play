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
    private readonly SampleRingBuffer _ring;
    private readonly int _outChannels;
    private readonly float[] _stereo; // només per a sortides de més de 2 canals

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

    /// <param name="ring">Memòria amb mostres estèreo intercalades.</param>
    /// <param name="sampleRate">Freqüència de la sortida.</param>
    /// <param name="outChannels">Canals de la sortida. L'estèreo va als dos primers; la resta, silenci.</param>
    /// <param name="gain">Guany inicial (el volum del reproductor).</param>
    public PlayerStream(SampleRingBuffer ring, int sampleRate, int outChannels, float gain = 1f)
    {
        _ring = ring;
        _outChannels = outChannels;
        _drainFrames = sampleRate * DrainMilliseconds / 1000;
        _gain = gain;
        _currentGain = gain; // sense rampa al principi
        _stereo = outChannels > 2 ? new float[sampleRate * 2] : [];
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, outChannels);
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

        ApplyGain(output, framesRead);
        Meter.AddInterleaved(output[..(framesRead * _outChannels)], _outChannels);

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
