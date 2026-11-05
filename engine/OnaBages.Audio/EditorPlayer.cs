namespace OnaBages.Audio;

public enum EditorPlayState
{
    Stopped,
    Playing,
    Error,
}

/// <summary>Tros de l'original que s'ha de reproduir, en segons.</summary>
public readonly record struct PlayPiece(double Start, double Length);

/// <summary>
/// Reproducció de l'editor (fase 3, pas 3; especificació 9.2): fa sonar la versió <b>editada</b>
/// d'un fitxer, és a dir, els trossos de l'original que es queden, un darrere l'altre, per una
/// sortida pròpia (per defecte, la de la preescolta). No té res a veure amb els reproductors
/// d'antena: un error aquí no els afecta.
///
/// Com els reproductors: un fil de lectura descodifica i omple una memòria intermèdia, i el fil
/// d'àudio (<see cref="PlayerStream"/>) només en copia mostres.
/// </summary>
public sealed class EditorPlayer : IDisposable
{
    private const int BufferSeconds = 3;
    private const string DeviceLostMessage = "La sortida de l'editor no està disponible: s'ha desconnectat o Windows l'ha reconfigurat.";

    private readonly Func<string, IAudioOutput> _openOutput;
    private readonly Lock _sync = new();

    private IAudioOutput? _output;
    private PlayerStream? _stream;
    private Thread? _reader;
    private volatile bool _stopReader;
    private volatile Exception? _readerError;
    private volatile Exception? _outputError;
    private double _startPosition;   // posició (de la versió editada) on ha començat a sonar
    private double _lastPosition;    // on s'ha aturat
    private double _endPosition;     // on acabarà

    /// <param name="openOutput">Obre una sortida per identificador. Per defecte, WASAPI; les proves en passen una de falsa.</param>
    public EditorPlayer(Func<string, IAudioOutput>? openOutput = null)
    {
        _openOutput = openOutput ?? (id => WasapiAudioOutput.Open(id));
    }

    public EditorPlayState State { get; private set; } = EditorPlayState.Stopped;

    /// <summary>Motiu de l'error, en català, per a l'operador.</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>Detall tècnic de l'error (per al registre).</summary>
    public string? ErrorDetail { get; private set; }

    /// <summary>Sortida per on sona (o ha sonat l'última vegada).</summary>
    public string? DeviceId { get; private set; }

    /// <summary>Posició a la versió editada, en segons: on sona ara o on s'ha aturat.</summary>
    public double Position
    {
        get
        {
            lock (_sync)
            {
                if (State != EditorPlayState.Playing || _stream is null || _output is null) return _lastPosition;
                double pos = _startPosition + (double)_stream.FramesPlayed / _output.SampleRate;
                return Math.Min(pos, _endPosition);
            }
        }
    }

    /// <summary>Pic del que surt, per al mesurador (0 si no sona).</summary>
    public (float Left, float Right) TakePeaks()
    {
        lock (_sync)
            return _stream?.Meter.Take() ?? (0, 0);
    }

    /// <summary>Canvi d'estat (no de posició).</summary>
    public event Action<EditorPlayer>? StateChanged;

    /// <summary>
    /// Fa sonar <paramref name="pieces"/> (trossos de l'original, en ordre) per <paramref name="deviceId"/>.
    /// <paramref name="editedStart"/> és la posició de la versió editada on comença el primer tros.
    /// Si ja sonava, primer s'atura.
    /// </summary>
    /// <param name="gainAt">Guany dels fosos en cada punt de l'original (segons → lineal), o null si no n'hi ha (fase 4, pas 6b).</param>
    public void Play(string deviceId, string path, IReadOnlyList<PlayPiece> pieces, double editedStart, Func<double, float>? gainAt = null)
    {
        lock (_sync)
        {
            Release();
            ErrorMessage = null;
            ErrorDetail = null;
            DeviceId = deviceId;
            _startPosition = editedStart;
            _lastPosition = editedStart;
            _endPosition = editedStart + pieces.Sum(p => p.Length);
            State = EditorPlayState.Stopped;
            Start(deviceId, path, pieces, gainAt);
        }
        StateChanged?.Invoke(this);
    }

    /// <summary>Obre la sortida i el fitxer i comença a sonar. Si alguna cosa falla, queda en Error (dins de _sync).</summary>
    private void Start(string deviceId, string path, IReadOnlyList<PlayPiece> pieces, Func<double, float>? gainAt)
    {
        if (pieces.Count == 0) return; // res a sonar

        AudioFileSource first;
        try
        {
            _output = _openOutput(deviceId);
            // El primer tros s'obre aquí: si el fitxer no es pot llegir, es diu de seguida.
            first = AudioFileSource.Open(path, _output.SampleRate, TimeSpan.FromSeconds(pieces[0].Start));
        }
        catch (Exception ex) when (ex is AudioDeviceException or AudioFileException)
        {
            Release();
            Fail(ex.Message, ex.InnerException?.Message);
            return;
        }

        var output = _output;
        int rate = output.SampleRate;
        var ring = new SampleRingBuffer(rate * 2 * BufferSeconds);
        var stream = new PlayerStream(ring, rate, output.Channels);
        _stream = stream;
        output.Stopped += error =>
        {
            // Fil de la sortida: només s'apunta; Tick() ho tractarà.
            if (error is not null && ReferenceEquals(output, _output)) _outputError = error;
        };
        output.Init(stream);

        _stopReader = false;
        _readerError = null;
        _outputError = null;
        _reader = new Thread(() => ReaderLoop(path, pieces, first, ring, stream, rate, gainAt))
        {
            IsBackground = true,
            Name = "Editor: lectura",
        };
        _reader.Start();

        try
        {
            // Com els reproductors: un moment perquè hi hagi so preparat abans de començar.
            var deadline = DateTime.UtcNow.AddSeconds(1);
            while (ring.Count < rate / 2 && !stream.SourceFinished && _readerError is null && DateTime.UtcNow < deadline)
                Thread.Sleep(5);
            output.Play();
            State = EditorPlayState.Playing;
        }
        catch (Exception ex)
        {
            Release();
            Fail(DeviceLostMessage, ex.Message);
        }
    }

    /// <summary>Atura. La posició es queda on era.</summary>
    public void Stop()
    {
        lock (_sync)
        {
            if (State == EditorPlayState.Stopped && _output is null) return;
            _lastPosition = Position_NoLock();
            Release();
            State = EditorPlayState.Stopped;
        }
        StateChanged?.Invoke(this);
    }

    /// <summary>Revisió periòdica (fora del fil d'àudio): final del que s'havia de sonar i errors.</summary>
    public void Tick()
    {
        bool changed = false;
        lock (_sync)
        {
            if (State != EditorPlayState.Playing || _stream is null) return;

            if (_outputError is { } outputError)
            {
                _lastPosition = Position_NoLock();
                Release();
                Fail(DeviceLostMessage, outputError.Message);
                changed = true;
            }
            else if (_readerError is { } readerError)
            {
                _lastPosition = Position_NoLock();
                Release();
                Fail("No s'ha pogut continuar llegint el fitxer: és malmès o ja no és accessible.", readerError.Message);
                changed = true;
            }
            else if (_stream.PlayedOut)
            {
                _lastPosition = _endPosition;
                Release();
                State = EditorPlayState.Stopped;
                changed = true;
            }
        }
        if (changed) StateChanged?.Invoke(this);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            Release();
            State = EditorPlayState.Stopped;
        }
    }

    // -----------------------------------------------------------------------

    private double Position_NoLock()
    {
        if (State != EditorPlayState.Playing || _stream is null || _output is null) return _lastPosition;
        return Math.Min(_startPosition + (double)_stream.FramesPlayed / _output.SampleRate, _endPosition);
    }

    /// <summary>Fil de lectura: cada tros, del principi a la llargada que toca, un darrere l'altre.</summary>
    private void ReaderLoop(string path, IReadOnlyList<PlayPiece> pieces, AudioFileSource first, SampleRingBuffer ring, PlayerStream stream, int rate, Func<double, float>? gainAt)
    {
        var chunk = new float[rate / 10 * 2]; // 100 ms
        try
        {
            for (int i = 0; i < pieces.Count && !_stopReader; i++)
            {
                using var source = i == 0
                    ? first
                    : AudioFileSource.Open(path, rate, TimeSpan.FromSeconds(pieces[i].Start));
                long remaining = (long)Math.Round(pieces[i].Length * rate) * 2;
                long framesRead = 0; // mostres per canal llegides d'aquest tros (per saber on som de l'original)

                while (remaining > 0 && !_stopReader)
                {
                    if (ring.Free < chunk.Length)
                    {
                        Thread.Sleep(10);
                        continue;
                    }
                    int read = source.Read(chunk.AsSpan(0, (int)Math.Min(chunk.Length, remaining)));
                    if (read == 0) break; // el fitxer s'acaba abans del que es pensava
                    // Fosos (fase 4, pas 6b): el guany del punt de l'original on és cada mostra.
                    if (gainAt is not null)
                    {
                        for (int s = 0; s + 1 < read; s += 2)
                        {
                            float gain = gainAt(pieces[i].Start + (double)(framesRead + s / 2) / rate);
                            chunk[s] *= gain;
                            chunk[s + 1] *= gain;
                        }
                    }
                    framesRead += read / 2;
                    ring.Write(chunk.AsSpan(0, read));
                    remaining -= read;
                }
            }
        }
        catch (Exception ex)
        {
            _readerError = ex;
        }
        finally
        {
            if (_stopReader) first.Dispose(); // aturat abans d'arribar-hi (si no, ja l'ha tancat el using)
            stream.MarkSourceFinished();
        }
    }

    private void Release()
    {
        _stopReader = true;
        _reader?.Join(TimeSpan.FromSeconds(2));
        _reader = null;
        _stream = null;
        if (_output is not null)
        {
            try { _output.Stop(); } catch { /* ja aturada o dispositiu perdut */ }
            try { _output.Dispose(); } catch { /* idem */ }
            _output = null;
        }
    }

    private void Fail(string message, string? detail)
    {
        ErrorMessage = message;
        ErrorDetail = detail;
        State = EditorPlayState.Error;
    }
}
