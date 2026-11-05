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

    private sealed record Transition(long StartFrame, PlaylistItem Item, TimeSpan Duration, double? NormalizationDb, TimeSpan StartOffset);

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

    /// <summary>
    /// Compressor/limitador del reproductor (fase 4, pas 4b; D-067). Es conserva d'una pista a l'altra.
    /// </summary>
    public DynamicsSettings Dynamics { get; private set; } = new();

    /// <summary>Canvia el compressor/limitador (en qualsevol estat; si sona, s'aplica de seguida i sense clics).</summary>
    public void SetDynamics(DynamicsSettings settings)
    {
        lock (_sync)
        {
            Dynamics = settings.Clamped();
            if (_stream is not null)
                _stream.Dynamics.Settings = Dynamics;
            _changed = true;
        }
        RaiseStateChanged();
    }

    // --- Encadenament amb fos (fase 5; D-077) ---

    /// <summary>Durada màxima del fos entre pistes encadenades.</summary>
    public const int MaxCrossfadeMs = 10_000;

    private volatile int _crossfadeMs;

    /// <summary>
    /// Fos entre pistes que s'encadenen soles (avanç automàtic), en mil·lisegons. 0 (per defecte) = sense fos.
    /// No s'aplica als bucles ni quan es fa sonar una pista a mà. Es llegeix en començar cada pista.
    /// </summary>
    public int CrossfadeMs => _crossfadeMs;

    /// <summary>Canvia la durada del fos (fora de marge, al límit). S'aplica a partir de la pista següent.</summary>
    public void SetCrossfade(int ms)
    {
        lock (_sync)
        {
            _crossfadeMs = Math.Clamp(ms, 0, MaxCrossfadeMs);
            _changed = true;
        }
        RaiseStateChanged();
    }

    /// <summary>Mostres (estèreo intercalades) que dura el fos a aquesta freqüència.</summary>
    private int CrossfadeSamples(int sampleRate) => (int)((long)_crossfadeMs * sampleRate / 1000) * 2;

    // --- Retall automàtic de silencis (fase 5, pas 8b; D-080) ---

    /// <summary>Ajustos del retall: llindar (dBFS, un de <see cref="TrackLoudness.SilenceThresholdsDb"/>) i marges en ms.</summary>
    public readonly record struct TrimSettings(bool Enabled, int ThresholdDb = -60, int LeadMs = 10, int TailMs = 50);

    public const int MaxTrimMarginMs = 2000;

    private volatile TrimSettingsBox _trim = new(new TrimSettings(false));

    /// <summary>Capsa immutable perquè el fil de lectura llegeixi els ajustos d'un sol cop (sense bloqueig).</summary>
    private sealed record TrimSettingsBox(TrimSettings Value);

    /// <summary>Retall de silencis del reproductor. Desactivat per defecte.</summary>
    public TrimSettings Trim => _trim.Value;

    /// <summary>
    /// Activa o desactiva el retall i en fixa el llindar i els marges (fora de marge, al límit; un llindar
    /// que no és dels que es mesuren, el més proper). S'aplica a partir de la pista següent.
    /// </summary>
    public void SetTrim(TrimSettings settings)
    {
        int threshold = TrackLoudness.SilenceThresholdsDb.MinBy(t => Math.Abs(t - settings.ThresholdDb));
        var clean = new TrimSettings(settings.Enabled, threshold,
            Math.Clamp(settings.LeadMs, 0, MaxTrimMarginMs), Math.Clamp(settings.TailMs, 0, MaxTrimMarginMs));
        lock (_sync)
        {
            _trim = new TrimSettingsBox(clean);
            _changed = true;
        }
        RaiseStateChanged();
    }

    /// <summary>
    /// On comença i on acaba de sonar una pista: amb el retall, del primer so (menys el marge) a l'últim
    /// (més el marge); si no, de <paramref name="requestedStart"/> al final. Si se n'ha demanat un començament
    /// concret (p. ex. un salt), es respecta. Es pot cridar des del fil de lectura (llegeix els ajustos d'un cop).
    /// </summary>
    private (TimeSpan Start, TimeSpan? End, bool Exact) TrimFor(PlaylistItem item, TimeSpan requestedStart)
    {
        var trim = _trim.Value;
        if (!trim.Enabled || item.Sound is null) return (requestedStart, null, false);
        SoundBounds? bounds = null;
        foreach (var b in item.Sound)
        {
            if (b.ThresholdDb == trim.ThresholdDb) bounds = b;
        }
        if (bounds is not { } sound) return (requestedStart, null, false); // tot silenci, o sense anàlisi: no es toca

        var end = TimeSpan.FromSeconds(sound.End + trim.TailMs / 1000.0);
        if (requestedStart > TimeSpan.Zero) return (requestedStart, end, false);
        var start = TimeSpan.FromSeconds(Math.Max(0, sound.Start - trim.LeadMs / 1000.0));
        return (start, end, start > TimeSpan.Zero);
    }

    /// <summary>Quantes mostres per canal es poden llegir d'una pista que comença a <paramref name="start"/> (−1 = fins al final).</summary>
    private static long FramesUntil(TimeSpan start, TimeSpan? end, int sampleRate) =>
        end is { } e ? Math.Max(0, (long)Math.Round((e - start).TotalSeconds * sampleRate)) : -1;

    /// <summary>
    /// Obre una pista per al fil de lectura: des de <paramref name="start"/> (a la mostra exacta si
    /// <paramref name="exact"/>) i, amb el retall, fins a <paramref name="end"/>.
    /// </summary>
    /// <exception cref="AudioFileException">Si no es pot llegir.</exception>
    private static TrackReader OpenTrack(PlaylistItem item, int sampleRate, TimeSpan start, TimeSpan? end, bool exact)
    {
        var source = AudioFileSource.Open(item.Path, sampleRate, start, exact);
        // El final no pot passar del final del fitxer.
        TimeSpan? limit = end is { } e && e < source.Duration ? e : null;
        return new TrackReader(source, start, limit is { } l ? l : source.Duration, FramesUntil(start, limit, sampleRate));
    }

    /// <summary>
    /// Una pista oberta per al fil de lectura: no en dona més mostres de les que toquen (amb el retall,
    /// para a l'últim so més el marge). <see cref="Duration"/> és on s'acaba de sonar (temps del fitxer).
    /// </summary>
    private sealed class TrackReader(AudioFileSource source, TimeSpan start, TimeSpan duration, long frames) : IDisposable
    {
        private long _remaining = frames < 0 ? long.MaxValue : frames * 2; // mostres (estèreo)

        /// <summary>On comença a sonar (temps del fitxer).</summary>
        public TimeSpan Start => start;

        /// <summary>On s'acaba de sonar (temps del fitxer): el final o, amb el retall, l'últim so més el marge.</summary>
        public TimeSpan Duration => duration;

        public int Read(Span<float> buffer)
        {
            if (_remaining <= 0) return 0;
            int read = source.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            _remaining -= read;
            return read;
        }

        public void Dispose() => source.Dispose();
    }

    // --- Normalització de sonoritat (fase 4, pas 5b; D-071) ---

    /// <summary>Guany màxim de la normalització: que no pugi massa el soroll de les pistes molt fluixes.</summary>
    public const double MaxNormalizationBoostDb = 10;

    /// <summary>Iguala la sonoritat de les pistes a <see cref="NormalizationTargetLufs"/>. S'aplica a partir de la pista següent.</summary>
    public bool Normalize { get; private set; }

    /// <summary>Objectiu de la normalització (LUFS).</summary>
    public double NormalizationTargetLufs { get; private set; } = -12;

    /// <summary>Guany de normalització de la pista que sona (dB), o null si no se n'aplica cap.</summary>
    public double? NormalizationDb { get; private set; }

    /// <summary>Activa o desactiva la normalització i en fixa l'objectiu. No canvia la pista que ja sona.</summary>
    public void SetNormalization(bool enabled, double targetLufs)
    {
        lock (_sync)
        {
            Normalize = enabled;
            NormalizationTargetLufs = targetLufs;
            _changed = true;
        }
        RaiseStateChanged();
    }

    /// <summary>Guany per a una pista amb la configuració d'ara (dB), o null si no se n'hi aplica cap.</summary>
    private double? NormalizationFor(PlaylistItem item) =>
        Normalize && item.LoudnessLufs is { } lufs
            ? Math.Round(Math.Min(MaxNormalizationBoostDb, NormalizationTargetLufs - lufs), 1)
            : null;

    /// <summary>Abaixada màxima del compressor/limitador des de l'última crida (dB, ≥ 0), per a l'indicador.</summary>
    public float TakeGainReductionDb()
    {
        var stream = _stream;
        return stream is null ? 0f : stream.Dynamics.TakeMaxReductionDb();
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

    /// <summary>
    /// Doble clic: atura el que sona (si n'hi ha) i fa sonar aquesta pista des de l'inici
    /// (o des de <paramref name="startAt"/>: la preescolta, per als botons de salt).
    /// </summary>
    public void PlayItem(string itemId, TimeSpan startAt = default)
    {
        lock (_sync)
        {
            var target = Playlist.Find(itemId)
                ?? throw new PlayerCommandException("Aquesta pista ja no és a la llista.");
            if (target.IsSection)
                throw new PlayerCommandException("Això és un separador de secció: no sona.");
            if (DeviceId is null)
                throw new PlayerCommandException("Cal triar abans una sortida per a aquest reproductor.");

            if (CurrentItemId is not null && State is PlayerState.Playing or PlayerState.Paused)
                LastPlayedId = CurrentItemId;
            // Amb fos configurat i una pista sonant: la nova entra amb fos (D-078). Si no, com sempre.
            if (State == PlayerState.Playing && _crossfadeMs > 0 && _stream is not null && _output is not null)
                CrossfadeTo(target, startAt < TimeSpan.Zero ? TimeSpan.Zero : startAt);
            else
                StartItem(itemId, startAt);
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
                    NormalizationDb = next.NormalizationDb;
                    _startOffset = next.StartOffset; // amb el retall, el primer so (temps del fitxer)
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
        // Canvi de pista amb fos que el fil d'àudio encara no ha fet: la pista nova comença ara.
        if (_stream.SwitchPending) return _startOffset;
        var position = _startOffset + TimeSpan.FromSeconds((double)(_stream.FramesPlayed - _trackStartFrame) / _output.SampleRate);
        if (position < TimeSpan.Zero) return TimeSpan.Zero;
        return position > Duration ? Duration : position;
    }

    /// <summary>Prepara una pista (des de l'inici, si no es diu res) i la fa sonar. Si tenia el cue, s'apaga.</summary>
    private void StartItem(string itemId, TimeSpan startAt = default)
    {
        PrepareItem(itemId, startAt < TimeSpan.Zero ? TimeSpan.Zero : startAt);
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

        // Retall de silencis (D-080): on comença i on acaba de sonar (sense retall, tota la pista).
        var (start, end, exact) = TrimFor(item, startAt);
        _keptPosition = start;
        _startOffset = start;

        TrackReader source;
        try
        {
            _output = _openOutput(DeviceId!);
            source = OpenTrack(item, _output.SampleRate, start, end, exact);
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
        _stream = new PlayerStream(ring, _output.SampleRate, _output.Channels, GainFor(VolumeDb), Dynamics);

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
        NormalizationDb = NormalizationFor(item);
        float gain = GainFor(NormalizationDb ?? 0);
        _readerThread = new Thread(() => ReaderLoop(source, ring, stream, sampleRate, currentId, gain))
        {
            IsBackground = true,
            Name = $"Reproductor {Id}: lectura",
        };
        _readerThread.Start();

        SetState(PlayerState.Ready);
    }

    /// <summary>
    /// Fa sonar una altra pista a mà amb fos (fase 5; D-078), sense aturar la sortida: la pista que sona baixa
    /// mentre la nova puja, durant <see cref="CrossfadeMs"/>. Si la pista nova no es pot obrir, la que sona
    /// continua com si res (i es diu per què). Dins de _sync, amb la pista sonant.
    /// </summary>
    private void CrossfadeTo(PlaylistItem item, TimeSpan startAt)
    {
        var output = _output!;
        var stream = _stream!;
        int sampleRate = output.SampleRate;

        // 1. La pista nova s'obre abans de tocar res: si no es pot, la que sona no s'interromp.
        var (start, end, exact) = TrimFor(item, startAt);
        startAt = start;
        TrackReader source;
        try
        {
            source = OpenTrack(item, sampleRate, start, end, exact);
        }
        catch (AudioFileException ex)
        {
            Playlist.MarkError(item.Id, ex.Message);
            throw new PlayerCommandException(ex.Message);
        }

        // 2. S'atura el fil de lectura de la que sona. La seva memòria conserva el que ja hi ha (uns segons):
        //    és el que sonarà, baixant, durant el fos.
        _stopReader = true;
        _readerThread?.Join(TimeSpan.FromSeconds(2));
        _readerThread = null;
        _transitions.Clear();

        // 3. Memòria i fil de lectura nous per a la pista nova.
        var ring = new SampleRingBuffer(sampleRate * 2 * BufferSeconds);
        _ring = ring;
        stream.PrepareForNewTrack();
        _stopReader = false;
        _readerError = null;
        string currentId = item.Id;
        NormalizationDb = NormalizationFor(item);
        float gain = GainFor(NormalizationDb ?? 0);
        _readerThread = new Thread(() => ReaderLoop(source, ring, stream, sampleRate, currentId, gain))
        {
            IsBackground = true,
            Name = $"Reproductor {Id}: lectura",
        };
        _readerThread.Start();

        // 4. Quan hi ha so preparat, el fil d'àudio hi passa amb el fos.
        WaitForPrefill();
        SetCurrent(item, source.Duration);
        _startOffset = startAt;
        _keptPosition = startAt;
        _trackStartFrame = 0;
        Playlist.ClearCueIf(item.Id);
        stream.SwitchTo(ring, (int)((long)_crossfadeMs * sampleRate / 1000));
        // L'estat continua sent «sonant», però la pista ha canviat: s'ha d'avisar la interfície.
        _changed = true;
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
    /// <param name="gain">Guany de normalització de la pista (lineal; 1 = cap). Cada pista encadenada porta el seu.</param>
    private void ReaderLoop(TrackReader source, SampleRingBuffer ring, PlayerStream stream, int sampleRate, string itemId, float gain)
    {
        var chunk = new float[ReadChunkSamples];
        var overflow = new float[ReadChunkSamples];
        long framesWritten = 0;
        long decideBelow = (long)(sampleRate * 2 * DecideBeforeEnd.TotalSeconds);

        // Encadenament amb fos (fase 5; D-077): els últims mil·lisegons de la pista es guarden a la cua
        // fins que se sap si n'ha de venir una altra sola. Amb 0 ms, tot passa de llarg (com sempre).
        var tail = new CrossfadeTail();
        float[] mix = [];   // la cua de la pista que s'acaba (i, després, la barreja)
        float[] head = [];  // el principi de la pista següent
        void ResetTail()
        {
            int samples = CrossfadeSamples(sampleRate);
            tail.Reset(samples);
            if (mix.Length < samples) mix = new float[samples];
            if (head.Length < samples) head = new float[samples];
        }
        ResetTail();

        // Escriu a la memòria (esperant si és plena) i compta els fotogrames.
        void Write(ReadOnlySpan<float> samples)
        {
            int written = 0;
            while (written < samples.Length && !_stopReader)
            {
                written += ring.Write(samples[written..]);
                if (written < samples.Length) Thread.Sleep(5);
            }
            framesWritten += written / 2;
        }

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
                        // No ve res més: la cua s'escriu tal qual (la pista acaba sencera).
                        Write(mix.AsSpan(0, tail.Drain(mix)));
                        stream.MarkSourceFinished();
                        return;
                    }

                    // Sense bloqueig: mentre s'atura, el reproductor pot estar esperant aquest fil amb el bloqueig agafat.
                    // Normalize i l'objectiu són valors simples (es llegeixen sencers).
                    double? normalization = NormalizationFor(next);
                    float nextGain = GainFor(normalization ?? 0);

                    // La cua: amb fos si la pista que ve és una altra (avanç automàtic); en un bucle, tal qual.
                    int held = tail.Drain(mix);
                    int overlap = 0;
                    if (held > 0 && next.Id != itemId)
                    {
                        // El principi de la pista següent, tan llarg com la cua (o menys, si és més curta).
                        int got = 0, r;
                        while (got < held && (r = nextSource.Read(head.AsSpan(got, held - got))) > 0) got += r;
                        for (int i = 0; i < got; i++) head[i] *= nextGain;
                        overlap = got & ~1; // mostres estèreo senceres
                    }

                    // El que no se sobreposa (si la pista següent és més curta que el fos, o en un bucle) va tal qual.
                    // Si el fos és més curt que la cua, la part que se sobreposa és el final de la cua.
                    Write(mix.AsSpan(0, held - overlap));
                    long startFrame = framesWritten; // la pista següent comença a sonar on comença el fos
                    if (overlap > 0)
                    {
                        var ending = mix.AsSpan(held - overlap, overlap);
                        CrossfadeTail.Mix(ending, head.AsSpan(0, overlap), overlap);
                        Write(ending);
                    }

                    source.Dispose();
                    source = nextSource;
                    itemId = next.Id;
                    gain = nextGain;
                    _transitions.Enqueue(new Transition(startFrame, next, nextSource.Duration, normalization, nextSource.Start));
                    ResetTail(); // la durada del fos es torna a llegir per a cada pista
                    continue;
                }

                // Normalització: el guany de la pista, abans que arribi al fil d'àudio.
                if (gain != 1f)
                {
                    for (int i = 0; i < read; i++) chunk[i] *= gain;
                }

                // Amb fos, només s'escriu el que sobra de la cua (les mostres més antigues).
                if (tail.Capacity > 0)
                {
                    if (overflow.Length < read) overflow = new float[read];
                    Write(overflow.AsSpan(0, tail.Push(chunk.AsSpan(0, read), overflow)));
                }
                else
                {
                    Write(chunk.AsSpan(0, read));
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

    /// <summary>Obre la pista següent; si no es pot, la marca a la llista i retorna null (la llista s'atura).</summary>
    private TrackReader? TryOpen(PlaylistItem item, int sampleRate)
    {
        try
        {
            // Una pista encadenada sempre comença des del principi (o del primer so, amb el retall).
            var (start, end, exact) = TrimFor(item, TimeSpan.Zero);
            return OpenTrack(item, sampleRate, start, end, exact);
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
