namespace OnaBages.Audio;

/// <summary>Estats del reproductor (especificació, 6.5). Fase 1: sense llista, ni «Esperant».</summary>
public enum PlayerState
{
    /// <summary>No hi ha cap pista carregada.</summary>
    Empty,
    /// <summary>Pista carregada i a punt per sonar.</summary>
    Ready,
    Playing,
    Paused,
    /// <summary>La pista s'ha acabat.</summary>
    Ended,
    /// <summary>Dispositiu no disponible o fitxer il·legible. Es conserva la pista.</summary>
    Error,
}

/// <summary>Ordre que no es pot fer en l'estat actual. El missatge és per a l'operador.</summary>
public sealed class PlayerCommandException(string message) : Exception(message);

/// <summary>
/// Un reproductor: una pista, una sortida, els seus propis fils.
///
/// Fils que hi intervenen:
/// - <b>ordres</b> (des del WebSocket) i <see cref="Tick"/>: sempre dins del bloqueig <c>_sync</c>;
/// - <b>lectura</b>: un fil propi que descodifica el fitxer i omple la memòria intermèdia;
/// - <b>àudio</b>: el de Windows, que només toca <see cref="PlayerStream"/>.
///
/// Un error d'aquest reproductor el posa en estat <see cref="PlayerState.Error"/>,
/// però no llança res cap als altres reproductors ni atura el motor.
/// </summary>
public sealed class Player : IDisposable
{
    /// <summary>Segons de so que el fil de lectura intenta tenir preparats.</summary>
    public const int BufferSeconds = 5;

    private const int ReadChunkSamples = 8192;

    private readonly Func<string, IAudioOutput> _openOutput;
    private readonly Lock _sync = new();

    private IAudioOutput? _output;
    private PlayerStream? _stream;
    private SampleRingBuffer? _ring;
    private Thread? _readerThread;
    private volatile bool _stopReader;
    private volatile Exception? _readerError;
    private volatile Exception? _outputError;
    private int _underrunsBefore; // talls de pistes anteriors (cada càrrega crea un PlayerStream nou)
    private TimeSpan _keptPosition; // posició conservada quan no hi ha sortida oberta (p. ex. en error)
    private TimeSpan _startOffset;  // on ha començat a llegir la pista actual (0, o la posició en reintentar)

    private const string DeviceLostMessage = "Dispositiu no disponible: s'ha desconnectat o Windows l'ha reconfigurat.";

    /// <param name="id">Identificador del reproductor ("1", "2"...).</param>
    /// <param name="openOutput">Obre una sortida per identificador de dispositiu. Per defecte, WASAPI.</param>
    public Player(string id, Func<string, IAudioOutput>? openOutput = null)
    {
        Id = id;
        _openOutput = openOutput ?? (deviceId => WasapiAudioOutput.Open(deviceId));
    }

    public string Id { get; }
    public PlayerState State { get; private set; } = PlayerState.Empty;
    public string? DeviceId { get; private set; }
    public string? FilePath { get; private set; }
    public TimeSpan Duration { get; private set; }

    /// <summary>Motiu de l'error, en català, per a l'operador.</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>Detall tècnic de l'error (sovint en anglès, de Windows). Només per al registre del motor.</summary>
    public string? ErrorDetail { get; private set; }

    /// <summary>Posició actual (el que ja s'ha enviat a la targeta).</summary>
    public TimeSpan Position
    {
        get { lock (_sync) return CurrentPosition(); }
    }

    /// <summary>Talls des que es va crear el reproductor (la sortida va demanar dades i no n'hi havia).</summary>
    public int Underruns
    {
        get { lock (_sync) return _underrunsBefore + (_stream?.Underruns ?? 0); }
    }

    /// <summary>Es dispara quan canvia l'estat (o el fitxer, el dispositiu o l'error).</summary>
    public event Action<Player>? StateChanged;

    // -----------------------------------------------------------------------
    // Ordres
    // -----------------------------------------------------------------------

    /// <summary>Assigna la sortida. No es pot canviar mentre sona.</summary>
    public void SetDevice(string deviceId)
    {
        lock (_sync)
        {
            if (State == PlayerState.Playing)
                throw new PlayerCommandException("No es pot canviar la sortida mentre sona. Atura'l primer.");

            // En pausa o en error es continua des del mateix punt; si no, des de l'inici.
            var resumeAt = State is PlayerState.Paused or PlayerState.Error ? CurrentPosition() : TimeSpan.Zero;
            DeviceId = deviceId;

            // Si hi havia una pista, es torna a preparar per la sortida nova.
            if (FilePath is not null)
                Prepare(FilePath, resumeAt);
            else
                SetState(PlayerState.Empty);
        }
        RaiseStateChanged();
    }

    /// <summary>Carrega un fitxer i el deixa a punt per sonar, des de l'inici.</summary>
    public void Load(string path)
    {
        lock (_sync)
        {
            if (State == PlayerState.Playing)
                throw new PlayerCommandException("No es pot carregar una pista mentre sona. Atura'l primer.");
            if (DeviceId is null)
                throw new PlayerCommandException("Cal triar abans una sortida per a aquest reproductor.");

            FilePath = path;
            Prepare(path, TimeSpan.Zero);
        }
        RaiseStateChanged();
    }

    /// <summary>
    /// Després d'un error, torna a obrir la sortida i el fitxer i deixa el reproductor
    /// preparat a la <b>mateixa posició</b>. No torna a sonar sol: cal prémer Play.
    /// </summary>
    public void Retry()
    {
        lock (_sync)
        {
            if (State != PlayerState.Error || FilePath is null)
                throw new PlayerCommandException("Només es pot reintentar quan el reproductor té un error.");

            Prepare(FilePath, _keptPosition);
        }
        RaiseStateChanged();
    }

    public void Play()
    {
        lock (_sync)
        {
            switch (State)
            {
                case PlayerState.Playing:
                    return;
                case PlayerState.Empty:
                    throw new PlayerCommandException("No hi ha cap pista carregada.");
                case PlayerState.Error:
                    throw new PlayerCommandException($"El reproductor té un error: {ErrorMessage} Prem Reintentar o tria una altra sortida.");
                case PlayerState.Ended:
                    Prepare(FilePath!, TimeSpan.Zero);
                    if (State == PlayerState.Error) break;
                    goto default;
                default:
                    try
                    {
                        WaitForPrefill();
                        _output!.Play();
                        SetState(PlayerState.Playing);
                    }
                    catch (Exception ex)
                    {
                        FailKeepingTrack(DeviceLostMessage, ex.Message);
                    }
                    break;
            }
        }
        RaiseStateChanged();
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (State != PlayerState.Playing)
                throw new PlayerCommandException("Només es pot posar en pausa mentre sona.");

            try
            {
                _output!.Pause();
                SetState(PlayerState.Paused);
            }
            catch (Exception ex)
            {
                FailKeepingTrack(DeviceLostMessage, ex.Message);
            }
        }
        RaiseStateChanged();
    }

    /// <summary>Atura i torna a l'inici de la pista.</summary>
    public void Stop()
    {
        lock (_sync)
        {
            if (State is PlayerState.Empty or PlayerState.Ready)
                return;
            if (State == PlayerState.Error)
                throw new PlayerCommandException($"El reproductor té un error: {ErrorMessage} Prem Reintentar o tria una altra sortida.");

            Prepare(FilePath!, TimeSpan.Zero);
        }
        RaiseStateChanged();
    }

    /// <summary>
    /// Revisió periòdica (unes 10 vegades per segon), fora del fil d'àudio:
    /// detecta el final de la pista i els errors del fil de lectura o de la sortida.
    /// </summary>
    public void Tick()
    {
        lock (_sync)
        {
            if (_outputError is { } outputError && State is not (PlayerState.Error or PlayerState.Empty))
            {
                _outputError = null;
                FailKeepingTrack(DeviceLostMessage, outputError.Message);
            }
            else if (_readerError is { } readerError && State is not (PlayerState.Error or PlayerState.Empty))
            {
                _readerError = null;
                FailKeepingTrack("No s'ha pogut continuar llegint el fitxer: és malmès o ja no és accessible.", readerError.Message);
            }
            else if (State == PlayerState.Playing && _stream!.EndReached)
            {
                _output!.Stop();
                SetState(PlayerState.Ended);
            }
        }
        RaiseStateChanged();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            ReleaseTrack();
            SetState(PlayerState.Empty);
        }
    }

    // -----------------------------------------------------------------------
    // Interns (sempre dins de _sync)
    // -----------------------------------------------------------------------

    /// <summary>Posició sense bloqueig (qui la crida ja és dins de _sync).</summary>
    private TimeSpan CurrentPosition()
    {
        if (_stream is null || _output is null) return _keptPosition;
        var position = _startOffset + TimeSpan.FromSeconds((double)_stream.FramesPlayed / _output.SampleRate);
        return position > Duration ? Duration : position;
    }

    /// <summary>
    /// Obre la sortida i el fitxer, a partir de <paramref name="startAt"/>, i comença a omplir la memòria.
    /// Si alguna cosa falla, el reproductor queda en Error amb la pista i la posició conservades.
    /// </summary>
    private void Prepare(string path, TimeSpan startAt)
    {
        ReleaseTrack();
        ErrorMessage = null;
        ErrorDetail = null;
        _keptPosition = startAt;
        _startOffset = startAt;

        AudioFileSource? source = null;
        try
        {
            _output = _openOutput(DeviceId!);
            source = AudioFileSource.Open(path, _output.SampleRate, startAt);
        }
        catch (Exception ex) when (ex is AudioDeviceException or AudioFileException)
        {
            ReleaseTrack();
            Fail(ex.Message, ex.InnerException?.Message);
            return;
        }

        Duration = source.Duration;
        var ring = new SampleRingBuffer(_output.SampleRate * 2 * BufferSeconds);
        _ring = ring;
        _stream = new PlayerStream(ring, _output.SampleRate, _output.Channels);

        var output = _output;
        output.Stopped += error =>
        {
            // Fil de la sortida: només s'apunta; Tick() ho tractarà.
            // Una sortida antiga (ja substituïda) no pot posar en error la nova.
            if (error is not null && ReferenceEquals(output, _output)) _outputError = error;
        };
        output.Init(_stream);

        _stopReader = false;
        _readerError = null;
        _outputError = null;
        var stream = _stream;
        _readerThread = new Thread(() => ReaderLoop(source, ring, stream))
        {
            IsBackground = true,
            Name = $"Reproductor {Id}: lectura",
        };
        _readerThread.Start();

        SetState(PlayerState.Ready);
    }

    /// <summary>
    /// Abans de començar a sonar, espera (com a màxim 1 s) que hi hagi mig segon de so preparat,
    /// o tot el fitxer si és més curt. Així no s'envia silenci a la targeta en prémer Play
    /// just després de carregar. S'executa al fil de les ordres, mai al d'àudio.
    /// </summary>
    private void WaitForPrefill()
    {
        int wanted = _output!.SampleRate; // 0,5 s en estèreo = SampleRate mostres
        var deadline = DateTime.UtcNow.AddSeconds(1);
        while (_ring!.Count < wanted && !_stream!.SourceFinished && _readerError is null && DateTime.UtcNow < deadline)
            Thread.Sleep(5);
    }

    /// <summary>Fil de lectura: descodifica el fitxer i manté la memòria plena.</summary>
    private void ReaderLoop(AudioFileSource source, SampleRingBuffer ring, PlayerStream stream)
    {
        var chunk = new float[ReadChunkSamples];
        try
        {
            while (!_stopReader)
            {
                if (ring.Free < chunk.Length)
                {
                    Thread.Sleep(10); // la memòria és plena: esperem que el fil d'àudio en consumeixi
                    continue;
                }

                int read = source.Read(chunk);
                if (read == 0)
                {
                    stream.MarkSourceFinished();
                    return;
                }

                // Hi ha lloc per a tot el tros (ho hem comprovat abans), però per si de cas:
                int written = 0;
                while (written < read && !_stopReader)
                {
                    written += ring.Write(chunk.AsSpan(written, read - written));
                    if (written < read) Thread.Sleep(5);
                }
            }
        }
        catch (Exception ex)
        {
            _readerError = ex;
            stream.MarkSourceFinished();
        }
        finally
        {
            source.Dispose();
        }
    }

    /// <summary>Atura els fils i allibera la sortida i el fitxer.</summary>
    private void ReleaseTrack()
    {
        _stopReader = true;
        _readerThread?.Join(TimeSpan.FromSeconds(2));
        _readerThread = null;

        if (_stream is not null)
        {
            _underrunsBefore += _stream.Underruns;
            _stream = null;
            _ring = null;
        }

        if (_output is not null)
        {
            try { _output.Stop(); } catch { /* ja aturada o dispositiu perdut */ }
            try { _output.Dispose(); } catch { /* idem */ }
            _output = null;
        }
    }

    /// <summary>Error: s'atura tot, però es conserva la pista per poder reintentar.</summary>
    private void FailKeepingTrack(string message, string? detail)
    {
        _keptPosition = CurrentPosition();
        ReleaseTrack();
        Fail(message, detail);
    }

    private void Fail(string message, string? detail)
    {
        ErrorMessage = message;
        ErrorDetail = detail;
        SetState(PlayerState.Error);
    }

    private bool _changed;

    private void SetState(PlayerState state)
    {
        State = state;
        _changed = true;
    }

    /// <summary>Avisa fora del bloqueig, perquè qui escolta pugui consultar el reproductor.</summary>
    private void RaiseStateChanged()
    {
        bool changed;
        lock (_sync)
        {
            changed = _changed;
            _changed = false;
        }
        if (changed) StateChanged?.Invoke(this);
    }
}
