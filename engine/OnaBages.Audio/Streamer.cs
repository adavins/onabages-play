using System.Collections.Concurrent;

namespace OnaBages.Audio;

/// <summary>Estat de l'emissor d'streaming.</summary>
public enum StreamerState
{
    /// <summary>No s'emet.</summary>
    Idle,

    /// <summary>S'ha premut «Inicia» i encara no s'ha pogut connectar (es continua provant).</summary>
    Connecting,

    /// <summary>Connectat: el so arriba al servidor.</summary>
    Streaming,

    /// <summary>S'havia connectat i s'ha tallat: es torna a provar sol.</summary>
    Reconnecting,

    /// <summary>L'emissió s'ha aturat per un error de l'entrada o del codificador.</summary>
    Error,
}

/// <summary>Una ordre que l'emissor no pot fer ara (p. ex. canviar la qualitat mentre s'emet).</summary>
public sealed class StreamerCommandException(string message) : Exception(message);

/// <summary>
/// Emissor d'streaming (fase 5, pas 31b; D-140): envia una entrada a un servidor Shoutcast o Icecast, com el BUTT.
///
/// Camí del so, cada part en el seu fil:
/// 1. <b>Fil de captura</b> (Windows): copia les mostres a una memòria (10 s) i al mesurador. Res més.
/// 2. <b>Fil de codificació</b>: estèreo → volum (fader) → NIV (anivellador) → DSP (compressor) → LIM (sostre)
///    → MP3 (LAME, que també canvia la freqüència si cal) → cua d'enviament.
/// 3. <b>Fil d'enviament</b>: es connecta, envia el que hi ha a la cua i, si la connexió es talla, es torna a connectar
///    sol. Mentre no està connectat, el so que surt del codificador es descarta (en directe no s'envia so endarrerit).
///
/// La xarxa no pot frenar mai la captura ni la codificació: si el servidor va lent, la cua s'omple (10 s) i el que
/// no hi cap es descarta i es compta (<see cref="BytesDropped"/>).
///
/// Els canvis d'estat que passen als fils (connectat, tallat, entrada perduda) es recullen a <see cref="Tick"/>,
/// que el motor crida sovint; <see cref="StateChanged"/> només es dispara des de les ordres i des de <see cref="Tick"/>.
///
/// Independent del motor de Play: el so entra per <see cref="IAudioInput"/>. A Ona Bages 24h, la font pot ser una
/// entrada de línia o la sortida del mesclador, sense canviar res d'aquí.
/// </summary>
public sealed class Streamer : IDisposable
{
    /// <summary>Segons d'àudio que caben a la memòria entre la captura i la codificació.</summary>
    public const int BufferSeconds = 10;

    /// <summary>Segons d'MP3 que caben a la cua d'enviament si la xarxa va lenta.</summary>
    public const int SendBufferSeconds = 10;

    /// <summary>Bitrates que es poden triar (MP3 CBR, kbps).</summary>
    public static readonly int[] Bitrates = [64, 80, 96, 112, 128, 160, 192, 224, 256, 320];

    /// <summary>Freqüències que es poden triar per a l'MP3.</summary>
    public static readonly int[] SampleRates = [44100, 48000];

    public const double MinGainDb = Recorder.MinGainDb, MaxGainDb = Recorder.MaxGainDb;
    public const double DefaultLimiterCeilingDb = Recorder.DefaultLimiterCeilingDb;
    /// <summary>Objectiu per defecte de l'anivellador: el mateix que la normalització dels reproductors.</summary>
    public const double DefaultLevelerTargetLufs = -12;

    private const int ChunkFrames = 4096;
    private static readonly TimeSpan MonitorRetry = TimeSpan.FromSeconds(3);

    private readonly Func<string, IAudioInput> _openInput;
    private readonly Action<StreamServer, string> _sendTitle;
    private readonly Lock _sync = new();
    private bool _changed;

    // Entrada: oberta sempre que hi ha dispositiu (per al mesurador), com als enregistradors.
    private IAudioInput? _input;
    private int _inputChannels;
    private volatile Exception? _inputError;
    private DateTime _nextMonitorAttempt;
    private float[] _zeros = [];

    // Només existeix mentre s'emet: si és null, el que arriba només va al mesurador.
    private volatile SampleRingBuffer? _ring;

    private Thread? _encoderThread, _senderThread;
    private volatile bool _stopEncoder, _stopSender;
    private volatile Exception? _encoderError;
    private readonly ManualResetEventSlim _wakeSender = new(false);

    // Cua d'enviament: l'omple el fil de codificació i la buida el d'enviament.
    private readonly ConcurrentQueue<byte[]> _queue = new();
    private long _queuedBytes, _queueLimit;
    private readonly AutoResetEvent _dataReady = new(false);

    // Ho escriu el fil d'enviament i ho llegeix Tick.
    private volatile bool _connected;
    private volatile string? _connectionError;
    private volatile StreamConnection? _connection;
    private int _connections; // vegades que s'ha connectat des de l'inici
    private long _bytesSent, _bytesDropped, _samplesLost, _framesCaptured;
    private long _connectedAtTicks;

    // Processament: el canvien les ordres i el llegeix el fil de codificació (una referència nova cada vegada).
    private volatile float _gain = 1f;
    private volatile bool _levelerEnabled;
    private volatile DynamicsSettings _dspSettings = new();
    private volatile DynamicsSettings _limiterSettings = LimiterSettings(false, DefaultLimiterCeilingDb);
    private volatile float _meterCeiling = float.MaxValue;
    private volatile DynamicsProcessor? _dsp, _limiter;
    private volatile Leveler? _leveler;

    /// <param name="id">Identificador ("1"...).</param>
    /// <param name="openInput">Obre una entrada per identificador de dispositiu. Per defecte, WASAPI.</param>
    /// <param name="sendTitle">Envia el títol al servidor. Per defecte, <see cref="StreamMetadata.SetTitle"/>.</param>
    public Streamer(string id, Func<string, IAudioInput>? openInput = null, Action<StreamServer, string>? sendTitle = null)
    {
        Id = id;
        _openInput = openInput ?? (deviceId => WasapiAudioInput.Open(deviceId));
        _sendTitle = sendTitle ?? StreamMetadata.SetTitle;
    }

    public string Id { get; }
    public StreamerState State { get; private set; } = StreamerState.Idle;
    public string? DeviceId { get; private set; }

    /// <summary>Servidor on s'emet (null = encara no se n'ha triat cap).</summary>
    public StreamServer? Server { get; private set; }

    /// <summary>Nom de l'emissora, gènere i web que veuen els oients.</summary>
    public StreamInfo Info { get; private set; } = new();

    public int BitrateKbps { get; private set; } = 128;
    public int SampleRate { get; private set; } = 44100;
    public bool Mono { get; private set; }

    /// <summary>Títol que veuen els oients (p. ex. «Directe des de la plaça»). Buit = no se n'envia cap.</summary>
    public string Title { get; private set; } = "";

    /// <summary>Per què no s'ha pogut enviar l'últim títol (l'emissió continua), o null.</summary>
    public string? TitleError { get; private set; }
    private volatile string? _titleError;

    public double GainDb { get; private set; }
    public bool LevelerEnabled { get; private set; }
    public double LevelerTargetLufs { get; private set; } = DefaultLevelerTargetLufs;
    public double LevelerMaxBoostDb { get; private set; } = Leveler.DefaultMaxBoostDb;
    public DynamicsSettings Dynamics { get; private set; } = new();
    public bool LimiterEnabled { get; private set; }
    public double LimiterCeilingDb { get; private set; } = DefaultLimiterCeilingDb;

    /// <summary>Pic del que entra, amb el volum aplicat (també quan no s'emet).</summary>
    public PeakMeter Meter { get; } = new();

    public string? ErrorMessage { get; private set; }
    public string? ErrorDetail { get; private set; }

    /// <summary>Mentre es connecta o es reconnecta: per què ha fallat l'últim intent (p. ex. «contrasenya»). Null si no.</summary>
    public string? ConnectionError { get; private set; }

    /// <summary>Quan s'ha premut «Inicia» (null si no s'emet).</summary>
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>Des de quan està connectat (null si no ho està).</summary>
    public DateTimeOffset? ConnectedAt { get; private set; }

    /// <summary>Vegades que s'ha hagut de tornar a connectar des de l'inici.</summary>
    public int Reconnects => Math.Max(0, Volatile.Read(ref _connections) - 1);

    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>Bytes descartats perquè la xarxa no anava prou de pressa (cua plena). Hauria de ser 0.</summary>
    public long BytesDropped => Interlocked.Read(ref _bytesDropped);

    /// <summary>Mostres perdudes perquè la memòria de captura era plena. Ha de ser 0.</summary>
    public long SamplesLost => Interlocked.Read(ref _samplesLost);

    /// <summary>Quant espera entre dos intents de connexió.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>L'entrada és oberta i el mesurador funciona.</summary>
    public bool InputActive
    {
        get { lock (_sync) return _input is not null && _inputError is null; }
    }

    /// <summary>S'està emetent (o intentant-ho): Connectant, Emetent o Reconnectant.</summary>
    public bool IsActive => State is StreamerState.Connecting or StreamerState.Streaming or StreamerState.Reconnecting;

    /// <summary>Guany de l'anivellador ara mateix (dB), o null si no treballa.</summary>
    public double? LevelerGainDb => _leveler?.CurrentGainDb;

    /// <summary>Quant ha abaixat el compressor (DSP) des de l'última crida (dB, ≥ 0).</summary>
    public float TakeDynamicsReductionDb() => _dsp?.TakeMaxReductionDb() ?? 0f;

    /// <summary>Quant ha abaixat el limitador (LIM) des de l'última crida (dB, ≥ 0).</summary>
    public float TakeLimiterReductionDb() => _limiter?.TakeMaxReductionDb() ?? 0f;

    /// <summary>Es dispara quan canvia l'estat o algun ajust. Mai des dels fils d'àudio ni de xarxa.</summary>
    public event Action<Streamer>? StateChanged;

    // -----------------------------------------------------------------------
    // Ordres
    // -----------------------------------------------------------------------

    public void SetDevice(string deviceId)
    {
        lock (_sync)
        {
            ThrowIfActive("No es pot canviar la font mentre s'emet.");
            if (deviceId != DeviceId) CloseInput();
            DeviceId = deviceId;
            OpenMonitor();
            _changed = true;
        }
        RaiseStateChanged();
    }

    /// <summary>Servidor i descripció de l'emissió (null = cap servidor). Només amb l'emissió aturada.</summary>
    public void SetServer(StreamServer? server, StreamInfo? info = null)
    {
        lock (_sync)
        {
            ThrowIfActive("No es pot canviar el servidor mentre s'emet.");
            Server = server;
            if (info is not null) Info = info;
            _changed = true;
        }
        RaiseStateChanged();
    }

    /// <summary>Qualitat de l'MP3. Només amb l'emissió aturada.</summary>
    public void SetQuality(int bitrateKbps, int sampleRate, bool mono)
    {
        if (!Bitrates.Contains(bitrateKbps))
            throw new StreamerCommandException($"Bitrate no vàlid: {bitrateKbps} kbps.");
        if (!SampleRates.Contains(sampleRate))
            throw new StreamerCommandException($"Freqüència no vàlida: {sampleRate} Hz.");
        lock (_sync)
        {
            ThrowIfActive("No es pot canviar la qualitat mentre s'emet.");
            BitrateKbps = bitrateKbps;
            SampleRate = sampleRate;
            Mono = mono;
            _changed = true;
        }
        RaiseStateChanged();
    }

    /// <summary>Canvia el títol. Si s'està emetent, s'envia de seguida al servidor (en un fil a part).</summary>
    public void SetTitle(string? title)
    {
        bool send;
        lock (_sync)
        {
            Title = StreamConnection.Clean(title);
            send = _connected;
            _changed = true;
        }
        if (send) SendTitleInBackground();
        RaiseStateChanged();
    }

    /// <summary>Volum (fader), en dB, de −60 a +12. S'aplica de seguida, amb una rampa curta (sense clics).</summary>
    public void SetGain(double db)
    {
        lock (_sync)
        {
            GainDb = Math.Round(Math.Clamp(double.IsFinite(db) ? db : 0, MinGainDb, MaxGainDb), 1);
            _gain = GainDb <= MinGainDb ? 0f : (float)Math.Pow(10, GainDb / 20);
            _changed = true;
        }
        RaiseStateChanged();
    }

    /// <summary>
    /// Anivellador (NIV). Es pot activar o desactivar mentre s'emet. Mira 1 s endavant: en activar-lo, l'emissió
    /// s'endarrereix 1 s (els oients no ho noten, el seu reproductor té més memòria); en desactivar-lo, aquest segon
    /// s'envia de cop. No es perd ni es repeteix cap mostra. L'objectiu i el màxim s'apliquen en tornar-lo a activar.
    /// </summary>
    public void SetLeveler(bool enabled, double? targetLufs = null, double? maxBoostDb = null)
    {
        lock (_sync)
        {
            LevelerEnabled = enabled;
            if (targetLufs is { } t && double.IsFinite(t)) LevelerTargetLufs = Math.Round(Math.Clamp(t, -30, -6), 1);
            if (maxBoostDb is { } m && double.IsFinite(m)) LevelerMaxBoostDb = Math.Clamp(m, 0, Leveler.MaxMaxBoostDb);
            _levelerEnabled = enabled;
            _changed = true;
        }
        RaiseStateChanged();
    }

    /// <summary>Compressor (DSP), com el dels reproductors. Es pot canviar mentre s'emet (sense clics).</summary>
    public void SetDynamics(DynamicsSettings settings)
    {
        lock (_sync)
        {
            Dynamics = settings.Clamped();
            _dspSettings = Dynamics;
            _changed = true;
        }
        RaiseStateChanged();
    }

    /// <summary>Limitador (LIM), com el dels enregistradors: el so no passa mai del sostre. Es pot canviar mentre s'emet.</summary>
    public void SetLimiter(bool enabled, double ceilingDb)
    {
        lock (_sync)
        {
            LimiterEnabled = enabled;
            LimiterCeilingDb = Math.Round(Math.Clamp(double.IsFinite(ceilingDb) ? ceilingDb : DefaultLimiterCeilingDb,
                Recorder.MinLimiterCeilingDb, Recorder.MaxLimiterCeilingDb), 1);
            _limiterSettings = LimiterSettings(enabled, LimiterCeilingDb);
            _meterCeiling = enabled ? (float)Math.Pow(10, LimiterCeilingDb / 20) : float.MaxValue;
            _changed = true;
        }
        RaiseStateChanged();
    }

    /// <summary>
    /// Comença a emetre: prepara el codificador i es connecta al servidor (en un fil a part; aquesta crida no espera la
    /// xarxa). Si no es pot connectar, ho continua provant fins que s'aturi.
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            ThrowIfActive("Ja s'està emetent.");
            if (DeviceId is null)
                throw new StreamerCommandException("Cal triar abans una font per a l'emissió.");
            if (Server is null || string.IsNullOrWhiteSpace(Server.Host))
                throw new StreamerCommandException("Cal triar abans un servidor d'streaming.");
            ErrorMessage = null;
            ErrorDetail = null;
            Begin();
        }
        RaiseStateChanged();
    }

    /// <summary>Atura l'emissió: envia el que quedava a la cua i tanca la connexió.</summary>
    public void Stop()
    {
        lock (_sync)
        {
            if (!IsActive) throw new StreamerCommandException("No s'està emetent.");
            Finish();
            SetState(StreamerState.Idle);
        }
        RaiseStateChanged();
    }

    /// <summary>
    /// Revisió periòdica (fora dels fils d'àudio i de xarxa): recull si s'ha connectat o tallat, si l'entrada s'ha
    /// perdut o si el codificador ha fallat, i torna a obrir el mesurador si l'entrada torna.
    /// </summary>
    public void Tick()
    {
        lock (_sync)
        {
            if (IsActive)
            {
                if (_inputError is { } inputError)
                {
                    Finish();
                    Fail("Font no disponible: s'ha desconnectat. L'emissió s'ha aturat.", inputError.Message);
                }
                else if (_encoderError is { } encoderError)
                {
                    Finish();
                    Fail("Error del codificador MP3. L'emissió s'ha aturat.", encoderError.Message);
                }
                else
                {
                    var wanted = _connected ? StreamerState.Streaming
                        : Volatile.Read(ref _connections) > 0 ? StreamerState.Reconnecting
                        : StreamerState.Connecting;
                    SetState(wanted);
                    var connectedAt = _connected ? new DateTimeOffset(Interlocked.Read(ref _connectedAtTicks), TimeSpan.Zero).ToLocalTime() : (DateTimeOffset?)null;
                    if (connectedAt != ConnectedAt) { ConnectedAt = connectedAt; _changed = true; }
                    string? error = _connected ? null : _connectionError;
                    if (error != ConnectionError) { ConnectionError = error; _changed = true; }
                }
            }
            else
            {
                // Sense emetre, perdre l'entrada no és cap error: el mesurador queda apagat i es torna a provar.
                if (_inputError is not null)
                {
                    CloseInput();
                    _nextMonitorAttempt = DateTime.UtcNow + MonitorRetry;
                    _changed = true;
                }
                if (_input is null && DeviceId is not null && DateTime.UtcNow >= _nextMonitorAttempt)
                {
                    OpenMonitor();
                    if (_input is not null) _changed = true;
                }
            }

            if (_titleError != TitleError) { TitleError = _titleError; _changed = true; }
        }
        RaiseStateChanged();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (IsActive) Finish();
            CloseInput();
        }
    }

    // -----------------------------------------------------------------------
    // Interns (dins de _sync)
    // -----------------------------------------------------------------------

    private void Begin()
    {
        IAudioInput? input = null;
        bool openedNow = false, encoderStarted = false;
        LameEncoder? encoder = null;
        try
        {
            if (_inputError is not null) CloseInput();
            if (_input is null)
            {
                input = _openInput(DeviceId!);
                AttachInput(input);
                openedNow = true;
            }
            else
            {
                input = _input;
            }

            var server = Server!;
            var format = new StreamFormat(BitrateKbps, SampleRate, Mono ? 1 : 2);
            var info = Info;
            encoder = new LameEncoder(input.SampleRate, BitrateKbps, mono: Mono, outputSampleRate: SampleRate);

            var ring = new SampleRingBuffer(input.SampleRate * input.Channels * BufferSeconds);
            _zeros = new float[input.SampleRate * input.Channels / 10];
            while (_queue.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _queuedBytes, 0);
            _queueLimit = BitrateKbps * 1000L / 8 * SendBufferSeconds;
            Interlocked.Exchange(ref _bytesSent, 0);
            Interlocked.Exchange(ref _bytesDropped, 0);
            Interlocked.Exchange(ref _samplesLost, 0);
            Interlocked.Exchange(ref _framesCaptured, 0);
            Volatile.Write(ref _connections, 0);
            _connected = false;
            _connectionError = null;
            _encoderError = null;
            _titleError = null;
            TitleError = null;
            ConnectionError = null;
            ConnectedAt = null;
            _stopEncoder = false;
            _stopSender = false;
            _wakeSender.Reset();

            var dsp = new DynamicsProcessor(input.SampleRate, _dspSettings);
            var limiter = new DynamicsProcessor(input.SampleRate, _limiterSettings);
            _dsp = dsp;
            _limiter = limiter;
            int inputRate = input.SampleRate;
            var encoderForThread = encoder;
            _encoderThread = new Thread(() => EncoderLoop(ring, encoderForThread, dsp, limiter, inputRate))
            {
                IsBackground = true,
                Name = $"Streaming {Id}: codificació",
                Priority = ThreadPriority.AboveNormal,
            };
            _senderThread = new Thread(() => SenderLoop(server, format, info))
            {
                IsBackground = true,
                Name = $"Streaming {Id}: enviament",
            };

            _encoderThread.Start();
            encoderStarted = true; // a partir d'ara, el codificador el tanca el seu fil
            _senderThread.Start();
            _ring = ring;
            if (openedNow) input.Start();
            StartedAt = DateTimeOffset.Now;
            SetState(StreamerState.Connecting);
        }
        catch (Exception ex)
        {
            _ring = null;
            if (openedNow) CloseInput();
            StopThreads();
            if (!encoderStarted) encoder?.Dispose();
            string message = ex is AudioDeviceException or AudioFileException ? ex.Message : "No s'ha pogut començar a emetre.";
            Fail(message, ex.InnerException?.Message ?? ex.Message);
        }
    }

    /// <summary>Deixa de rebre so, buida el codificador, envia el que queda a la cua (màx. 3 s) i tanca la connexió.</summary>
    private void Finish()
    {
        _ring = null;
        Thread.Sleep(30); // el fil de captura pot estar acabant d'escriure l'últim paquet
        StopThreads();
        _dsp = null;
        _limiter = null;
        _leveler = null;
        StartedAt = null;
        ConnectedAt = null;
        ConnectionError = null;
        if (_inputError is not null) CloseInput();
    }

    private void StopThreads()
    {
        _stopEncoder = true;
        _encoderThread?.Join(TimeSpan.FromSeconds(10));
        _encoderThread = null;

        _stopSender = true;
        _wakeSender.Set();
        _dataReady.Set();
        if (_senderThread is { } sender && !sender.Join(TimeSpan.FromSeconds(3)))
        {
            // La xarxa no respon: es tanca la connexió perquè el fil s'acabi ara.
            _connection?.Dispose();
            sender.Join(TimeSpan.FromSeconds(2));
        }
        _senderThread = null;
        _connected = false;
    }

    private void Fail(string message, string? detail)
    {
        ErrorMessage = message;
        ErrorDetail = detail;
        SetState(StreamerState.Error);
    }

    private void SetState(StreamerState state)
    {
        if (State == state) return;
        State = state;
        _changed = true;
    }

    private void ThrowIfActive(string message)
    {
        if (IsActive) throw new StreamerCommandException(message);
    }

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

    private void SendTitleInBackground()
    {
        var server = Server;
        string title = Title;
        if (server is null || title.Length == 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                _sendTitle(server, title);
                _titleError = null;
            }
            catch (Exception ex)
            {
                _titleError = ex is StreamException ? ex.Message : "No s'ha pogut enviar el títol.";
            }
        });
    }

    // -----------------------------------------------------------------------
    // Entrada (com als enregistradors)
    // -----------------------------------------------------------------------

    private void OpenMonitor()
    {
        if (_input is not null || DeviceId is null) return;
        IAudioInput? input = null;
        try
        {
            input = _openInput(DeviceId);
            AttachInput(input);
            input.Start();
        }
        catch
        {
            if (input is not null) CloseInput();
            _nextMonitorAttempt = DateTime.UtcNow + MonitorRetry;
        }
    }

    private void AttachInput(IAudioInput input)
    {
        _input = input;
        _inputChannels = input.Channels;
        _inputError = null;
        input.DataAvailable += OnCaptured;
        input.Stopped += error => { if (error is not null && ReferenceEquals(input, _input)) _inputError = error; };
    }

    private void CloseInput()
    {
        var input = _input;
        if (input is null) return;
        _input = null;
        _inputError = null;
        input.DataAvailable -= OnCaptured;
        try { input.Stop(); } catch { /* entrada ja perduda */ }
        try { input.Dispose(); } catch { /* idem */ }
    }

    /// <summary>Fil de captura de Windows: copia les mostres a la memòria i al mesurador. Res més.</summary>
    private void OnCaptured(ReadOnlySpan<float> samples, bool silent, bool discontinuity)
    {
        int channels = _inputChannels;
        if (!silent) Meter.AddInterleaved(samples, channels, _gain, _meterCeiling);

        var ring = _ring;
        if (ring is null) return;

        // Només fotogrames sencers, perquè els canals no es desordenin si la memòria s'omple.
        int written = 0;
        while (written < samples.Length)
        {
            int free = ring.Free - ring.Free % channels;
            int chunk = Math.Min(samples.Length - written, silent ? Math.Min(free, _zeros.Length) : free);
            if (chunk <= 0) break;
            written += ring.Write(silent ? _zeros.AsSpan(0, chunk) : samples.Slice(written, chunk));
        }
        if (written < samples.Length)
            Interlocked.Add(ref _samplesLost, samples.Length - written);
        Interlocked.Add(ref _framesCaptured, samples.Length / channels);
    }

    // -----------------------------------------------------------------------
    // Fil de codificació
    // -----------------------------------------------------------------------

    /// <summary>
    /// Memòria → estèreo → volum → [NIV] → DSP → LIM → MP3 → cua d'enviament.
    /// L'anivellador «estira» el so (llegeix 1 s endavant): per això la lectura de la memòria és una funció que espera
    /// fins que hi ha so, i que retorna 0 quan s'atura l'emissió o quan s'ha de desactivar l'anivellador (així surt
    /// tot el que tenia dins, sense perdre res).
    /// </summary>
    private void EncoderLoop(SampleRingBuffer ring, LameEncoder encoder, DynamicsProcessor dsp, DynamicsProcessor limiter, int sampleRate)
    {
        int channels = _inputChannels;
        var input = new float[ChunkFrames * channels];
        var stereo = new float[ChunkFrames * 2];
        float appliedGain = _gain;
        var appliedDsp = dsp.Settings;
        var appliedLimiter = limiter.Settings;
        Leveler? leveler = null;

        // So de la memòria, en estèreo i amb el volum, a «destination». Espera fins que n'hi ha.
        int ReadRaw(Span<float> destination)
        {
            int maxFrames = Math.Min(destination.Length / 2, ChunkFrames);
            while (true)
            {
                if (leveler is not null && !_levelerEnabled) return 0; // desactivar: que surti el que té dins
                int read = ring.Read(input.AsSpan(0, maxFrames * channels));
                if (read > 0)
                {
                    int frames = read / channels;
                    var output = destination[..(frames * 2)];
                    for (int i = 0; i < frames; i++)
                    {
                        float left = input[i * channels];
                        output[i * 2] = left;
                        output[i * 2 + 1] = channels == 1 ? left : input[i * channels + 1];
                    }
                    appliedGain = ApplyGain(output, frames, appliedGain, _gain);
                    return frames * 2;
                }
                if (_stopEncoder) return 0;
                Thread.Sleep(10);
            }
        }

        void EncodeAndQueue(int samples)
        {
            var settings = _dspSettings;
            if (!ReferenceEquals(settings, appliedDsp)) { dsp.Settings = settings; appliedDsp = settings; }
            var limit = _limiterSettings;
            if (!ReferenceEquals(limit, appliedLimiter)) { limiter.Settings = limit; appliedLimiter = limit; }

            var span = stereo.AsSpan(0, samples);
            dsp.Process(span);
            limiter.Process(span);
            for (int i = 0; i < samples; i++) stereo[i] = Math.Clamp(stereo[i], -1f, 1f);
            Enqueue(encoder.Encode(stereo, samples));
        }

        try
        {
            while (true)
            {
                if (_levelerEnabled && leveler is null)
                {
                    leveler = new Leveler(ReadRaw, sampleRate, LevelerTargetLufs, LevelerMaxBoostDb);
                    _leveler = leveler;
                }

                int samples = leveler is not null ? leveler.Read(stereo) : ReadRaw(stereo);
                if (samples > 0)
                {
                    EncodeAndQueue(samples);
                    continue;
                }

                if (leveler is not null)
                {
                    // L'anivellador s'ha buidat (desactivat o aturada): a partir d'ara, sense.
                    leveler = null;
                    _leveler = null;
                    if (!_stopEncoder) continue;
                }
                if (_stopEncoder && ring.Count == 0) break;
            }

            // Els processadors encara tenen dins seu l'últim 1,5 ms de so: s'hi passa silenci perquè surti.
            int pending = Math.Max(dsp.LatencyFrames, limiter.LatencyFrames) * 2;
            if (pending > 0)
            {
                Array.Clear(stereo, 0, pending);
                EncodeAndQueue(pending);
            }
            Enqueue(encoder.Flush());
        }
        catch (Exception ex)
        {
            _encoderError = ex;
        }
        finally
        {
            encoder.Dispose();
        }
    }

    /// <summary>Posa l'MP3 a la cua si hi ha connexió (si no, es descarta: en directe no s'envia so endarrerit).</summary>
    private void Enqueue(ReadOnlySpan<byte> mp3)
    {
        if (mp3.Length == 0 || !_connected) return;
        if (Interlocked.Read(ref _queuedBytes) + mp3.Length > _queueLimit)
        {
            Interlocked.Add(ref _bytesDropped, mp3.Length);
            return;
        }
        _queue.Enqueue(mp3.ToArray());
        Interlocked.Add(ref _queuedBytes, mp3.Length);
        _dataReady.Set();
    }

    /// <summary>
    /// Volum en un bloc estèreo. Si ha canviat, passa de l'anterior al nou al llarg del bloc (sense clics).
    /// Retorna el guany del final del bloc.
    /// </summary>
    private static float ApplyGain(Span<float> stereo, int frames, float from, float to)
    {
        if (from == 1f && to == 1f) return to;
        float step = frames > 0 ? (to - from) / frames : 0;
        float gain = from;
        for (int i = 0; i < frames; i++)
        {
            gain += step;
            stereo[i * 2] *= gain;
            stereo[i * 2 + 1] *= gain;
        }
        return to;
    }

    private static DynamicsSettings LimiterSettings(bool enabled, double ceilingDb) =>
        new(enabled, InputGainDb: 0, ThresholdDb: 0, Ratio: 1, OutputGainDb: 0, StereoLink: true, CeilingDb: ceilingDb);

    // -----------------------------------------------------------------------
    // Fil d'enviament
    // -----------------------------------------------------------------------

    /// <summary>
    /// Es connecta, envia la cua i, si es talla, es torna a connectar (cada <see cref="RetryDelay"/>) fins que s'atura.
    /// En aturar, envia el que quedava a la cua abans de tancar.
    /// </summary>
    private void SenderLoop(StreamServer server, StreamFormat format, StreamInfo info)
    {
        while (!_stopSender)
        {
            StreamConnection connection;
            try
            {
                connection = StreamConnection.Open(server, format, info);
            }
            catch (Exception ex)
            {
                _connectionError = ex is StreamException ? ex.Message : "No es pot connectar amb el servidor.";
                _wakeSender.Wait(RetryDelay);
                continue;
            }

            _connection = connection;
            Interlocked.Exchange(ref _connectedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
            Interlocked.Increment(ref _connections);
            _connectionError = null;
            _connected = true; // a partir d'ara, el codificador posa l'MP3 a la cua
            SendTitleInBackground();

            try
            {
                while (true)
                {
                    if (_queue.TryDequeue(out var chunk))
                    {
                        Interlocked.Add(ref _queuedBytes, -chunk.Length);
                        connection.Write(chunk);
                        Interlocked.Add(ref _bytesSent, chunk.Length);
                    }
                    else if (_stopSender)
                    {
                        break; // aturat i cua buida
                    }
                    else
                    {
                        _dataReady.WaitOne(100);
                    }
                }
            }
            catch (Exception ex)
            {
                _connectionError = ex is StreamException ? ex.Message : "S'ha perdut la connexió amb el servidor.";
            }
            finally
            {
                _connected = false;
                _connection = null;
                connection.Dispose();
                // El que hi havia a la cua ja és vell: no s'enviarà en tornar a connectar.
                while (_queue.TryDequeue(out _)) { }
                Interlocked.Exchange(ref _queuedBytes, 0);
            }

            // Un moment abans de tornar a provar (o de seguida, si s'atura).
            if (!_stopSender) _wakeSender.Wait(TimeSpan.FromMilliseconds(Math.Min(1000, RetryDelay.TotalMilliseconds)));
        }
    }
}
