using System.Collections.Concurrent;

namespace OnaBages.Audio;

/// <summary>Estats del reproductor (especificació 6.5).</summary>
public enum PlayerState
{
    /// <summary>Encara no ha sonat res (o la llista és buida).</summary>
    Empty,
    /// <summary>Pista preparada i a punt per sonar (p. ex. després de Reintentar).</summary>
    Ready,
    Playing,
    Paused,
    /// <summary>
    /// Aturat: la pista s'ha acabat (avanç manual) o s'ha premut Stop. La pista queda com a
    /// «última reproduïda»; Play fa sonar la següent (o la del cue).
    /// </summary>
    Ended,
    /// <summary>Dispositiu no disponible o fitxer il·legible. Es conserva la pista i la posició.</summary>
    Error,
}

/// <summary>Ordre que no es pot fer en l'estat actual. El missatge és per a l'operador.</summary>
public sealed class PlayerCommandException(string message) : Exception(message);

/// <summary>
/// Un reproductor: una llista, una sortida, els seus propis fils.
///
/// Fils que hi intervenen:
/// - <b>ordres</b> (des del WebSocket) i <see cref="Tick"/>: sempre dins del bloqueig <c>_sync</c>;
/// - <b>lectura</b>: un fil propi que descodifica el fitxer i omple la memòria intermèdia.
///   Quan una pista s'acaba i n'ha de venir una altra (bucle o avanç automàtic), hi continua
///   escrivint la següent sense tancar la sortida: no hi ha silenci entre pistes;
/// - <b>àudio</b>: el de Windows, que només toca <see cref="PlayerStream"/>.
///
/// Un error d'aquest reproductor el posa en estat <see cref="PlayerState.Error"/>,
/// però no llança res cap als altres reproductors ni atura el motor.
/// </summary>
public sealed class Player : IDisposable
{
    /// <summary>Segons de so que el fil de lectura intenta tenir preparats.</summary>
    public const int BufferSeconds = 5;

    /// <summary>
    /// Quan s'acaba de llegir una pista, el fil de lectura espera que en quedi aquest so per sonar
    /// abans de decidir si n'encadena una altra. Així els canvis de cue, bucle o avanç automàtic
    /// fets fins a aquest moment encara compten.
    /// </summary>
    public static readonly TimeSpan DecideBeforeEnd = TimeSpan.FromSeconds(1);

    /// <summary>Volum mínim del fader: en aquest punt el reproductor queda en silenci.</summary>
    public const double MinVolumeDb = -60;

    private const int ReadChunkSamples = 8192;
    private const string DeviceLostMessage = "Dispositiu no disponible: s'ha desconnectat o Windows l'ha reconfigurat.";

    private readonly Func<string, IAudioOutput> _openOutput;
    private readonly Lock _sync = new();

    private IAudioOutput? _output;
    private PlayerStream? _stream;
    private SampleRingBuffer? _ring;
    private Thread? _readerThread;
    private volatile bool _stopReader;
    private volatile Exception? _readerError;
    private volatile Exception? _outputError;
    private int _underrunsBefore; // talls de pistes anteriors (cada preparació crea un PlayerStream nou)
    private TimeSpan _keptPosition; // posició conservada quan no hi ha sortida oberta (p. ex. en error)
    private TimeSpan _startOffset;  // on ha començat a llegir la pista actual (0, o la posició en saltar/reintentar)
    private long _trackStartFrame;  // mostra de la sortida on ha començat la pista actual
    private string? _format;        // format de la pista actual (es conserva si s'esborra de la llista)
    private bool _changed;

    /// <summary>Pistes encadenades pel fil de lectura que encara no han començat a sonar.</summary>
    private readonly ConcurrentQueue<Transition> _transitions = new();

    private sealed record Transition(long StartFrame, PlaylistItem Item, TimeSpan Duration);

    /// <param name="id">Identificador del reproductor ("1", "2"...).</param>
    /// <param name="openOutput">Obre una sortida per identificador de dispositiu. Per defecte, WASAPI.</param>
    /// <param name="analyzer">Comprova les pistes de la llista en segon pla.</param>
    public Player(string id, Func<string, IAudioOutput>? openOutput = null, TrackAnalyzer? analyzer = null)
    {
        Id = id;
        _openOutput = openOutput ?? (deviceId => WasapiAudioOutput.Open(deviceId));
        Playlist = new Playlist(analyzer);
    }

    public string Id { get; }
    public PlayerState State { get; private set; } = PlayerState.Empty;
    public string? DeviceId { get; private set; }
    public Playlist Playlist { get; }

    /// <summary>Pista de la llista que sona (o preparada, en pausa, en error). Null si no n'hi ha.</summary>
    public string? CurrentItemId { get; private set; }

    /// <summary>Última pista que ha sonat (es marca en blau). Play fa sonar la següent.</summary>
    public string? LastPlayedId { get; private set; }

    /// <summary>Fitxer de la pista actual.</summary>
    public string? FilePath { get; private set; }

    public TimeSpan Duration { get; private set; }

    /// <summary>Format abreujat de la pista actual (p. ex. «MP3 192 kbps estèreo 44,1 kHz»), si ja es coneix.</summary>
    public string? Format
    {
        get
        {
            lock (_sync)
                return Playlist.Find(CurrentItemId)?.Format ?? (CurrentItemId is null ? null : _format);
        }
    }

    /// <summary>Volum en dB, de <see cref="MinVolumeDb"/> (silenci) a 0 (sense canvi). En arrencar, sempre 0.</summary>
    public double VolumeDb { get; private set; }

    /// <summary>Motiu de l'error, en català, per a l'operador.</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>Detall tècnic de l'error (sovint en anglès, de Windows). Només per al registre del motor.</summary>
    public string? ErrorDetail { get; private set; }

    /// <summary>Posició dins la pista actual (el que ja s'ha enviat a la targeta).</summary>
    public TimeSpan Position
    {
        get { lock (_sync) return CurrentPosition(); }
    }

    /// <summary>Talls des que es va crear el reproductor (la sortida va demanar dades i no n'hi havia).</summary>
    public int Underruns
    {
        get { lock (_sync) return _underrunsBefore + (_stream?.Underruns ?? 0); }
    }

    /// <summary>Es dispara quan canvia l'estat, la pista, el dispositiu, el volum o l'error.</summary>
    public event Action<Player>? StateChanged;

    // -----------------------------------------------------------------------
    // Ordres
    // -----------------------------------------------------------------------

    /// <summary>Canvia el volum (en qualsevol estat, també mentre sona). Fora de marge, es queda al límit.</summary>
    public void SetVolume(double db)
    {
        lock (_sync)
        {
            VolumeDb = Math.Round(Math.Clamp(db, MinVolumeDb, 0), 1);
            if (_stream is not null)
                _stream.Gain = GainFor(VolumeDb);
            _changed = true;
        }
        RaiseStateChanged();
    }

    /// <summary>Pic del que ha sortit des de l'última crida, per al mesurador (0 si no sona).</summary>
    public (float Left, float Right) TakePeaks()
    {
        var stream = _stream;
        return stream is null ? (0f, 0f) : stream.Meter.Take();
    }

    /// <summary>Guany lineal d'un volum en dB. Al mínim del fader, silenci.</summary>
    public static float GainFor(double db) =>
        db <= MinVolumeDb ? 0f : (float)Math.Pow(10, db / 20);

    /// <summary>Assigna la sortida. No es pot canviar mentre sona.</summary>
    public void SetDevice(string deviceId)
    {
        lock (_sync)
        {
            if (State == PlayerState.Playing)
                throw new PlayerCommandException("No es pot canviar la sortida mentre sona. Atura'l primer.");

            DeviceId = deviceId;
            _changed = true;

            // Si hi havia una pista preparada, es torna a preparar per la sortida nova
            // (des del mateix punt si era en pausa o en error).
            if (CurrentItemId is not null && State is PlayerState.Ready or PlayerState.Paused or PlayerState.Error)
            {
                var resumeAt = State is PlayerState.Paused or PlayerState.Error ? CurrentPosition() : _startOffset;
                bool wasPaused = State == PlayerState.Paused;
                PrepareItem(CurrentItemId, resumeAt);
                if (wasPaused && State == PlayerState.Ready) SetState(PlayerState.Paused);
            }
        }
        RaiseStateChanged();
    }

    /// <summary>
    /// Afegeix un fitxer al final de la llista i el deixa preparat per sonar (si no sona res).
    /// Es manté per a la consola de proves i per a les proves; la interfície fa servir la llista.
    /// </summary>
    public void Load(string path)
    {
        lock (_sync)
        {
            if (State == PlayerState.Playing)
                throw new PlayerCommandException("No es pot carregar una pista mentre sona. Afegeix-la a la llista.");
            if (DeviceId is null)
                throw new PlayerCommandException("Cal triar abans una sortida per a aquest reproductor.");

            string id = Playlist.Add([path])[0];
            ReleaseTrack();
            PrepareItem(id, TimeSpan.Zero);
        }
        RaiseStateChanged();
    }

    /// <summary>Doble clic: atura el que sona (si n'hi ha) i fa sonar aquesta pista des de l'inici.</summary>
    public void PlayItem(string itemId)
    {
        lock (_sync)
        {
            if (Playlist.Find(itemId) is null)
                throw new PlayerCommandException("Aquesta pista ja no és a la llista.");
            if (DeviceId is null)
                throw new PlayerCommandException("Cal triar abans una sortida per a aquest reproductor.");

            if (CurrentItemId is not null && State is PlayerState.Playing or PlayerState.Paused)
                LastPlayedId = CurrentItemId;
            StartItem(itemId);
        }
        RaiseStateChanged();
    }

    /// <summary>
    /// Play. En pausa o preparat, continua la pista actual. Si no, fa sonar la pista amb cue o,
    /// si no n'hi ha, la que va després de l'última reproduïda (D-031).
    /// </summary>
    public void Play()
    {
        lock (_sync)
        {
            switch (State)
            {
                case PlayerState.Playing:
                    return;
                case PlayerState.Error:
                    throw new PlayerCommandException($"El reproductor té un error: {ErrorMessage} Prem Reintentar o tria una altra sortida.");
                case PlayerState.Ready or PlayerState.Paused:
                    StartOutput();
                    break;
                default: // Empty, Ended
                    if (DeviceId is null)
                        throw new PlayerCommandException("Cal triar abans una sortida per a aquest reproductor.");
                    string? next = Playlist.CueId ?? Playlist.NextAfter(LastPlayedId);
                    if (next is null)
                        throw new PlayerCommandException(Playlist.Items.Count == 0
                            ? "La llista és buida."
                            : "S'ha arribat al final de la llista. Fes doble clic a una pista o marca'n una amb cue.");
                    StartItem(next);
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

    /// <summary>
    /// Salta a un punt de la pista actual. Si sonava, continua sonant des d'allà (amb un silenci
    /// molt curt mentre es torna a obrir la sortida); si era en pausa, hi continua.
    /// Tornar a començar la pista és saltar a 0.
    /// </summary>
    public void Seek(TimeSpan position)
    {
        lock (_sync)
        {
            if (CurrentItemId is null || State is PlayerState.Empty or PlayerState.Ended or PlayerState.Error)
                throw new PlayerCommandException("No hi ha cap pista preparada on saltar.");

            var previous = State;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (position > Duration) position = Duration;

            PrepareItem(CurrentItemId, position);
            if (State == PlayerState.Ready)
            {
                if (previous == PlayerState.Playing) StartOutput();
                else if (previous == PlayerState.Paused) SetState(PlayerState.Paused);
            }
        }
        RaiseStateChanged();
    }

    /// <summary>Stop: s'atura la llista. La pista queda com a última reproduïda; Play farà sonar la següent.</summary>
    public void Stop()
    {
        lock (_sync)
        {
            if (State is PlayerState.Empty or PlayerState.Ended)
                return;
            if (State == PlayerState.Error)
                throw new PlayerCommandException($"El reproductor té un error: {ErrorMessage} Prem Reintentar o tria una altra sortida.");

            FinishList();
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
            if (State != PlayerState.Error || CurrentItemId is null)
                throw new PlayerCommandException("Només es pot reintentar quan el reproductor té un error.");

            PrepareItem(CurrentItemId, _keptPosition);
        }
        RaiseStateChanged();
    }

    /// <summary>
    /// Revisió periòdica (unes 10 vegades per segon), fora del fil d'àudio: detecta el pas
    /// d'una pista a la següent, el final i els errors del fil de lectura o de la sortida.
    /// </summary>
    public void Tick()
    {
        lock (_sync)
        {
            if (_outputError is { } outputError && State is not (PlayerState.Error or PlayerState.Empty or PlayerState.Ended))
            {
                _outputError = null;
                FailKeepingTrack(DeviceLostMessage, outputError.Message);
            }
            else if (_readerError is { } readerError && State is not (PlayerState.Error or PlayerState.Empty or PlayerState.Ended))
            {
                _readerError = null;
                FailKeepingTrack("No s'ha pogut continuar llegint el fitxer: és malmès o ja no és accessible.", readerError.Message);
            }
            else if (_stream is not null)
            {
                // Pistes encadenades que ja han començat a sonar (bucle o avanç automàtic).
                while (_transitions.TryPeek(out var next) && _stream.FramesPlayed >= next.StartFrame)
                {
                    _transitions.TryDequeue(out _);

                    // Per precaució, l'avanç automàtic ja fet es desmarca: la pista no tornarà a
                    // encadenar sola si es torna a fer sonar (D-033). El bucle sí que es manté.
                    if (CurrentItemId is not null && CurrentItemId != next.Item.Id
                        && Playlist.Find(CurrentItemId) is { AutoNext: true })
                        Playlist.SetFlags(CurrentItemId, autoNext: false);

                    LastPlayedId = CurrentItemId;
                    SetCurrent(next.Item, next.Duration);
                    _startOffset = TimeSpan.Zero;
                    _trackStartFrame = next.StartFrame;
                    Playlist.ClearCueIf(next.Item.Id);
                    _changed = true;
                }

                if (State == PlayerState.Playing && _stream.PlayedOut)
                    FinishList();
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
        var position = _startOffset + TimeSpan.FromSeconds((double)(_stream.FramesPlayed - _trackStartFrame) / _output.SampleRate);
        if (position < TimeSpan.Zero) return TimeSpan.Zero;
        return position > Duration ? Duration : position;
    }

    /// <summary>Prepara una pista des de l'inici i la fa sonar. Si tenia el cue, s'apaga.</summary>
    private void StartItem(string itemId)
    {
        PrepareItem(itemId, TimeSpan.Zero);
        if (State != PlayerState.Ready) return; // error en obrir-la: ja ho diu l'estat
        Playlist.ClearCueIf(itemId);
        StartOutput();
    }

    private void StartOutput()
    {
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
    }

    /// <summary>S'ha acabat (o s'ha premut Stop): la pista queda com a última reproduïda.</summary>
    private void FinishList()
    {
        LastPlayedId = CurrentItemId ?? LastPlayedId;
        ReleaseTrack();
        CurrentItemId = null;
        FilePath = null;
        Duration = TimeSpan.Zero;
        _keptPosition = TimeSpan.Zero;
        SetState(PlayerState.Ended);
    }

    private void SetCurrent(PlaylistItem item, TimeSpan duration)
    {
        CurrentItemId = item.Id;
        FilePath = item.Path;
        Duration = duration;
        _format = item.Format;
    }

    /// <summary>
    /// Obre la sortida i la pista, a partir de <paramref name="startAt"/>, i comença a omplir la memòria.
    /// Si alguna cosa falla, el reproductor queda en Error amb la pista i la posició conservades.
    /// </summary>
    private void PrepareItem(string itemId, TimeSpan startAt)
    {
        ReleaseTrack();
        ErrorMessage = null;
        ErrorDetail = null;
        _keptPosition = startAt;
        _startOffset = startAt;
        _trackStartFrame = 0;

        var item = Playlist.Find(itemId);
        if (item is null)
        {
            // La pista s'ha esborrat de la llista (p. ex. en pausa): ja no es pot tornar a obrir.
            CurrentItemId = null;
            FilePath = null;
            SetState(PlayerState.Ended);
            return;
        }
        SetCurrent(item, item.Duration ?? TimeSpan.Zero);

        AudioFileSource? source = null;
        try
        {
            _output = _openOutput(DeviceId!);
            source = AudioFileSource.Open(item.Path, _output.SampleRate, startAt);
        }
        catch (Exception ex) when (ex is AudioDeviceException or AudioFileException)
        {
            ReleaseTrack();
            if (ex is AudioFileException) Playlist.MarkError(item.Id, ex.Message);
            Fail(ex.Message, ex.InnerException?.Message);
            return;
        }

        Duration = source.Duration;
        var ring = new SampleRingBuffer(_output.SampleRate * 2 * BufferSeconds);
        _ring = ring;
        _stream = new PlayerStream(ring, _output.SampleRate, _output.Channels, GainFor(VolumeDb));

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
        int sampleRate = _output.SampleRate;
        string currentId = item.Id;
        _readerThread = new Thread(() => ReaderLoop(source, ring, stream, sampleRate, currentId))
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

    /// <summary>
    /// Pista que ha de sonar quan s'acabi <paramref name="itemId"/>, sense aturar-se (D-031):
    /// ella mateixa si té el bucle; la del cue o la següent si té l'avanç automàtic; si no, cap.
    /// La crida el fil de lectura: només fa servir la llista (que té el seu propi bloqueig).
    /// </summary>
    private PlaylistItem? ChooseContinuation(string itemId)
    {
        var item = Playlist.Find(itemId);
        if (item is null) return null; // s'ha esborrat mentre sonava: s'atura
        if (item.Loop) return item;
        if (!item.AutoNext) return null;
        return Playlist.Find(Playlist.CueId ?? Playlist.NextAfter(itemId));
    }

    /// <summary>
    /// Fil de lectura: descodifica la pista i manté la memòria plena. Quan s'acaba, espera fins que
    /// en queda <see cref="DecideBeforeEnd"/> per sonar i, si n'ha de venir una altra, l'obre i hi
    /// continua escrivint: així no hi ha silenci entre pistes.
    /// </summary>
    private void ReaderLoop(AudioFileSource source, SampleRingBuffer ring, PlayerStream stream, int sampleRate, string itemId)
    {
        var chunk = new float[ReadChunkSamples];
        long framesWritten = 0;
        long decideBelow = (long)(sampleRate * 2 * DecideBeforeEnd.TotalSeconds);
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
                    // Final de la pista: s'espera l'últim segon per decidir què ve després.
                    while (!_stopReader && ring.Count > decideBelow)
                        Thread.Sleep(10);
                    if (_stopReader) return;

                    var next = ChooseContinuation(itemId);
                    var nextSource = next is null ? null : TryOpen(next, sampleRate);
                    if (next is null || nextSource is null)
                    {
                        stream.MarkSourceFinished();
                        return;
                    }

                    source.Dispose();
                    source = nextSource;
                    itemId = next.Id;
                    _transitions.Enqueue(new Transition(framesWritten, next, nextSource.Duration));
                    continue;
                }

                // Hi ha lloc per a tot el tros (ho hem comprovat abans), però per si de cas:
                int written = 0;
                while (written < read && !_stopReader)
                {
                    written += ring.Write(chunk.AsSpan(written, read - written));
                    if (written < read) Thread.Sleep(5);
                }
                framesWritten += written / 2;
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

    /// <summary>Obre la pista següent; si no es pot, la marca a la llista i retorna null (la llista s'atura).</summary>
    private AudioFileSource? TryOpen(PlaylistItem item, int sampleRate)
    {
        try
        {
            return AudioFileSource.Open(item.Path, sampleRate);
        }
        catch (AudioFileException ex)
        {
            Playlist.MarkError(item.Id, ex.Message);
            return null;
        }
    }

    /// <summary>Atura els fils i allibera la sortida i el fitxer.</summary>
    private void ReleaseTrack()
    {
        _stopReader = true;
        _readerThread?.Join(TimeSpan.FromSeconds(2));
        _readerThread = null;
        _transitions.Clear();

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
