using System.Diagnostics;
using System.Text.Json.Nodes;

namespace OnaBages.Audio;

/// <summary>Estats de l'enregistrador (especificació, 8.3). Desar i Descartar: RecordingList (motor); Editar: fase 3.</summary>
public enum RecorderState
{
    /// <summary>Sense enregistrament en curs ni fitxer pendent.</summary>
    Idle,
    Recording,
    /// <summary>Enregistrament acabat correctament; el fitxer és a <see cref="Recorder.FilePath"/>.</summary>
    Pending,
    /// <summary>Entrada perduda, disc ple... Si hi havia fitxer, s'ha tancat i és vàlid fins on arriba.</summary>
    Error,
}

/// <summary>Ordre que no es pot fer en l'estat actual. El missatge és per a l'operador.</summary>
public sealed class RecorderCommandException(string message) : Exception(message);

/// <summary>Un enregistrament acabat (vegeu <see cref="Recorder.Finished"/>).</summary>
/// <param name="ClosedCleanly">El fitxer s'ha tancat bé. Si és false, queda el marcador .rec per recuperar-lo.</param>
public sealed record FinishedRecording(
    string RecorderId, string Path, DateTimeOffset StartedAt, TimeSpan Duration, long FileSize, bool ClosedCleanly);

/// <summary>
/// Un enregistrador: una entrada → un fitxer MP3 escrit directament (LAME, 320 kbps CBR, estèreo).
///
/// Fils:
/// - <b>captura</b> (Windows): només copia les mostres a la memòria intermèdia i compta;
/// - <b>codificació</b> (propi): llegeix la memòria, codifica amb LAME, escriu al fitxer
///   i força l'escriptura física al disc cada <see cref="FlushInterval"/>;
/// - <b>ordres</b> i <see cref="Tick"/>: dins del bloqueig <c>_sync</c>.
///
/// Mentre s'enregistra hi ha un marcador <c>fitxer.mp3.rec</c> al costat; s'esborra quan
/// l'enregistrament es tanca correctament. Si el procés cau, el marcador queda i el fitxer
/// es pot recuperar (fase 2).
/// </summary>
public sealed class Recorder : IDisposable
{
    /// <summary>Segons d'àudio que caben a la memòria entre la captura i la codificació.</summary>
    public const int BufferSeconds = 10;

    /// <summary>Cada quant es força l'escriptura física al disc.</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    /// <summary>Carpeta per defecte: Documents\Ona Bages Play\Enregistraments.</summary>
    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Ona Bages Play", "Enregistraments");

    private const int ChunkFrames = 4096;

    private readonly Func<string, IAudioInput> _openInput;
    private readonly Lock _sync = new();

    /// <summary>Si l'entrada no s'ha pogut obrir per al mesurador, cada quant es torna a provar.</summary>
    private static readonly TimeSpan MonitorRetry = TimeSpan.FromSeconds(3);

    // L'entrada és oberta sempre que hi ha dispositiu (per al mesurador), no només mentre s'enregistra.
    private IAudioInput? _input;
    // Només existeix mentre s'enregistra: si és null, el que arriba només va al mesurador.
    private volatile SampleRingBuffer? _ring;
    private DateTime _nextMonitorAttempt;
    private float[] _zeros = [];
    private Thread? _encoderThread;
    private volatile bool _stopEncoder;
    private volatile Exception? _inputError;
    private volatile Exception? _encoderError;
    private long _framesCaptured;
    private long _samplesLost;
    private int _captureGlitches;
    private long _fileSize;
    private int _inputChannels;
    private bool _changed;
    // Volum d'enregistrament (lineal). El llegeixen el fil de captura (mesurador) i el de codificació.
    private volatile float _gain = 1f;

    /// <summary>Volum d'enregistrament mínim: a partir d'aquí és silenci.</summary>
    public const double MinGainDb = -60;

    /// <summary>Volum d'enregistrament màxim (+12 dB, per a fonts que entren fluixes).</summary>
    public const double MaxGainDb = 12;

    // --- Limitador (fase 5, pas 9a; D-083) ---

    /// <summary>Sostre per defecte del limitador: −1 dB, perquè l'MP3 descodificat tampoc no passi de 0 dBFS.</summary>
    public const double DefaultLimiterCeilingDb = -1.0;
    public const double MinLimiterCeilingDb = -6, MaxLimiterCeilingDb = 0;

    // Els ajustos del limitador: els canvia el fil de les ordres i els llegeix el de codificació (una referència nova cada vegada).
    private volatile DynamicsSettings _limiterSettings = LimiterSettings(false, DefaultLimiterCeilingDb);
    // El limitador de l'enregistrament en curs (és del fil de codificació), per a l'indicador de reducció.
    private volatile DynamicsProcessor? _limiter;
    // Sostre del limitador per al mesurador (lineal; sense limitador, cap). El llegeix el fil de captura.
    private volatile float _meterCeiling = float.MaxValue;

    /// <param name="id">Identificador ("1", "2"...).</param>
    /// <param name="openInput">Obre una entrada per identificador de dispositiu. Per defecte, WASAPI.</param>
    public Recorder(string id, Func<string, IAudioInput>? openInput = null)
    {
        Id = id;
        _openInput = openInput ?? (deviceId => WasapiAudioInput.Open(deviceId));
        Name = DefaultName;
    }

    public string Id { get; }
    public RecorderState State { get; private set; } = RecorderState.Idle;
    public string? DeviceId { get; private set; }

    /// <summary>Nom de l'enregistrador (p. ex. «Programa»): surt al panell i al nom dels fitxers.</summary>
    public string Name { get; private set; }

    /// <summary>Carpeta de treball, on es creen els enregistraments.</summary>
    public string Folder { get; private set; } = DefaultFolder;

    public string DefaultName => $"Enregistrador {Id}";

    /// <summary>Volum d'enregistrament en dB (de <see cref="MinGainDb"/> a <see cref="MaxGainDb"/>; per defecte 0).</summary>
    public double GainDb { get; private set; }

    /// <summary>
    /// Queda poc espai: menys de l'equivalent a <see cref="LowDiskHours"/> hores d'enregistrament.
    /// </summary>
    public bool LowDiskSpace => FreeDiskBytes is { } free && free < (long)(BitrateKbps * 1000L / 8 * 3600 * LowDiskHours);

    /// <summary>Hores d'enregistrament per sota de les quals s'avisa que queda poc espai.</summary>
    public double LowDiskHours { get; set; } = 2;

    /// <summary>
    /// S'ha acabat un enregistrament (aturat, entrada perduda o motor que s'atura) i el fitxer és tancat.
    /// Es dispara fora del bloqueig.
    /// </summary>
    public event Action<FinishedRecording>? Finished;

    /// <summary>Fitxer de l'enregistrament en curs o de l'últim.</summary>
    public string? FilePath { get; private set; }

    public int SampleRate { get; private set; }
    public int BitrateKbps { get; private set; } = 320;
    public DateTimeOffset? StartedAt { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? ErrorDetail { get; private set; }

    /// <summary>Temps enregistrat (capturat).</summary>
    public TimeSpan Elapsed => SampleRate > 0
        ? TimeSpan.FromSeconds((double)Interlocked.Read(ref _framesCaptured) / SampleRate)
        : TimeSpan.Zero;

    /// <summary>Bytes escrits al fitxer.</summary>
    public long FileSize => Interlocked.Read(ref _fileSize);

    /// <summary>Mostres perdudes perquè la memòria era plena (el disc no ha anat prou ràpid). Ha de ser 0.</summary>
    public long SamplesLost => Interlocked.Read(ref _samplesLost);

    /// <summary>Vegades que Windows ha avisat que s'havien perdut dades de captura. Ha de ser 0.</summary>
    public int CaptureGlitches => Volatile.Read(ref _captureGlitches);

    /// <summary>Espai lliure a la unitat del fitxer, o null si no es pot saber (p. ex. unitat de xarxa).</summary>
    public long? FreeDiskBytes
    {
        get
        {
            try
            {
                // Mentre s'enregistra, el disc del fitxer; si no, el de la carpeta on anirà el següent.
                string? root = Path.GetPathRoot(State == RecorderState.Recording && FilePath is not null ? FilePath : Folder);
                return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Pic del que entra (també quan no s'enregistra), per al mesurador.</summary>
    public PeakMeter Meter { get; } = new();

    /// <summary>L'entrada és oberta i el mesurador funciona.</summary>
    public bool InputActive
    {
        get { lock (_sync) return _input is not null && _inputError is null; }
    }

    /// <summary>Es dispara quan canvia l'estat, el fitxer, el dispositiu o l'error.</summary>
    public event Action<Recorder>? StateChanged;

    // -----------------------------------------------------------------------
    // Ordres
    // -----------------------------------------------------------------------

    public void SetDevice(string deviceId)
    {
        lock (_sync)
        {
            if (State == RecorderState.Recording)
                throw new RecorderCommandException("No es pot canviar l'entrada mentre s'enregistra.");
            if (deviceId != DeviceId)
                CloseInput();
            DeviceId = deviceId;
            OpenMonitor();
            _changed = true;
        }
        RaiseStateChanged();
    }

    /// <summary>
    /// Canvia el nom i/o la carpeta de treball (els que no siguin null). Es pot fer en qualsevol
    /// moment: s'aplica a partir del pròxim enregistrament. Un nom buit torna al de per defecte.
    /// </summary>
    public void Configure(string? name = null, string? folder = null)
    {
        lock (_sync)
        {
            if (folder is not null)
            {
                if (!Path.IsPathFullyQualified(folder))
                    throw new RecorderCommandException("La carpeta ha de ser una ruta completa (p. ex. D:\\Enregistraments).");
                Folder = folder;
                _changed = true;
            }
            if (name is not null)
            {
                Name = string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim();
                _changed = true;
            }
        }
        RaiseStateChanged();
    }

    /// <summary>
    /// Volum d'enregistrament, en dB. Fora de marge, es queda al límit; al mínim és silenci.
    /// S'aplica de seguida (amb una rampa curta, sense clics), també a mig enregistrament.
    /// </summary>
    public void SetGain(double db)
    {
        lock (_sync)
        {
            GainDb = Math.Round(Math.Clamp(db, MinGainDb, MaxGainDb), 1);
            _gain = GainDb <= MinGainDb ? 0f : (float)Math.Pow(10, GainDb / 20);
            _changed = true;
        }
        RaiseStateChanged();
    }

    /// <summary>El limitador és activat (el so que va al fitxer no passa de <see cref="LimiterCeilingDb"/>).</summary>
    public bool LimiterEnabled { get; private set; }

    /// <summary>Sostre del limitador (dBFS).</summary>
    public double LimiterCeilingDb { get; private set; } = DefaultLimiterCeilingDb;

    /// <summary>
    /// Limitador del que es desa al fitxer (després del volum d'enregistrament): el so no passa mai del sostre.
    /// Mira 1,5 ms endavant: abaixa el volum just abans d'un cop, sense retallar-lo. Sense compressió: per sota del
    /// sostre, el so no canvia. Es pot activar i desactivar a mig enregistrament (amb una transició de 10 ms, sense clics).
    /// El sostre fora de marge es porta al límit.
    /// </summary>
    public void SetLimiter(bool enabled, double ceilingDb)
    {
        lock (_sync)
        {
            LimiterEnabled = enabled;
            LimiterCeilingDb = Math.Round(Math.Clamp(double.IsFinite(ceilingDb) ? ceilingDb : DefaultLimiterCeilingDb,
                MinLimiterCeilingDb, MaxLimiterCeilingDb), 1);
            _limiterSettings = LimiterSettings(enabled, LimiterCeilingDb);
            _meterCeiling = enabled ? (float)Math.Pow(10, LimiterCeilingDb / 20) : float.MaxValue;
            _changed = true;
        }
        RaiseStateChanged();
    }

    /// <summary>Quant ha abaixat el limitador des de l'última crida (dB, ≥ 0). 0 si no s'enregistra.</summary>
    public float TakeLimiterReductionDb() => _limiter?.TakeMaxReductionDb() ?? 0f;

    /// <summary>El limitador és el sostre del compressor dels reproductors, sense compressió (ràtio 1).</summary>
    private static DynamicsSettings LimiterSettings(bool enabled, double ceilingDb) =>
        new(enabled, InputGainDb: 0, ThresholdDb: 0, Ratio: 1, OutputGainDb: 0, StereoLink: true, CeilingDb: ceilingDb);

    /// <summary>
    /// Comença un enregistrament nou a la carpeta de treball, amb el nom de l'enregistrador.
    /// Es pot fer des d'Inactiu, Pendent o Error (l'enregistrament anterior es queda on és).
    /// Mai no se sobreescriu cap fitxer.
    /// </summary>
    /// <param name="folder">Només per a aquest enregistrament. Per defecte, <see cref="Folder"/>.</param>
    /// <param name="name">Només per a aquest enregistrament. Per defecte, <see cref="Name"/>.</param>
    public void Start(string? folder = null, string? name = null)
    {
        lock (_sync)
        {
            if (State == RecorderState.Recording)
                throw new RecorderCommandException("Ja s'està enregistrant.");
            if (DeviceId is null)
                throw new RecorderCommandException("Cal triar abans una entrada per a aquest enregistrador.");

            ErrorMessage = null;
            ErrorDetail = null;
            BeginRecording(folder ?? Folder, name ?? Name);
        }
        RaiseStateChanged();
    }

    /// <summary>Atura l'enregistrament, tanca el fitxer (vàlid) i passa a Pendent.</summary>
    public void Stop()
    {
        FinishedRecording? finished;
        lock (_sync)
        {
            if (State != RecorderState.Recording)
                throw new RecorderCommandException("No s'està enregistrant.");

            bool ok = FinishRecording();
            finished = Snapshot(ok);
            if (ok)
                SetState(RecorderState.Pending);
            else
                Fail("No s'ha pogut tancar bé el fitxer. Es podrà recuperar en tornar a arrencar.", _encoderError?.Message);
        }
        RaiseFinished(finished);
        RaiseStateChanged();
    }

    /// <summary>
    /// L'enregistrament pendent ja s'ha desat o descartat: l'enregistrador torna a Inactiu.
    /// Si no és el fitxer pendent (p. ex. ja se n'ha començat un altre), no fa res.
    /// </summary>
    public void Release(string path)
    {
        lock (_sync)
        {
            if (State != RecorderState.Pending || !string.Equals(FilePath, path, StringComparison.OrdinalIgnoreCase))
                return;
            // L'enregistrament ja és a la llista: l'enregistrador queda net, com si fos nou.
            FilePath = null;
            StartedAt = null;
            Interlocked.Exchange(ref _framesCaptured, 0);
            Interlocked.Exchange(ref _fileSize, 0);
            SetState(RecorderState.Idle);
        }
        RaiseStateChanged();
    }

    /// <summary>
    /// Revisió periòdica, fora dels fils d'àudio: detecta si l'entrada s'ha perdut
    /// o si la codificació ha fallat (p. ex. disc ple).
    /// </summary>
    public void Tick()
    {
        FinishedRecording? finished = null;
        lock (_sync)
        {
            if (State != RecorderState.Recording)
            {
                // Sense enregistrar, perdre l'entrada no és un error (no es perd res): el mesurador
                // queda apagat i es torna a obrir l'entrada quan torni.
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
            else if (_inputError is { } inputError)
            {
                // Especificació 5.3: es tanca el fitxer de manera neta i s'avisa.
                bool ok = FinishRecording();
                finished = Snapshot(ok);
                Fail(ok
                    ? "Entrada no disponible: s'ha desconnectat. L'enregistrament s'ha desat fins aquí; pots començar-ne un de nou."
                    : "Entrada no disponible i no s'ha pogut tancar bé el fitxer. Es podrà recuperar en tornar a arrencar.",
                    inputError.Message);
            }
            else if (_encoderError is { } encoderError)
            {
                FinishRecording();
                finished = Snapshot(ok: false);
                Fail("No es pot escriure el fitxer (disc ple o inaccessible). Es podrà recuperar en tornar a arrencar.",
                    encoderError.Message);
            }
        }
        RaiseFinished(finished);
        RaiseStateChanged();
    }

    public void Dispose()
    {
        FinishedRecording? finished = null;
        lock (_sync)
        {
            if (State == RecorderState.Recording)
                finished = Snapshot(FinishRecording());
            CloseInput();
        }
        // En aturar el motor: l'enregistrament queda a la llista de recents per a la propera vegada.
        RaiseFinished(finished);
    }

    // -----------------------------------------------------------------------
    // Interns (dins de _sync)
    // -----------------------------------------------------------------------

    private void BeginRecording(string folder, string name)
    {
        IAudioInput? input = null;
        bool openedNow = false;
        FileStream? file = null;
        LameEncoder? encoder = null;
        string? path = null;
        try
        {
            // Normalment l'entrada ja és oberta (mesurador): l'enregistrament comença a l'instant.
            // Si s'havia perdut o no s'havia pogut obrir, es torna a obrir ara.
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

            Directory.CreateDirectory(folder);
            var startedAt = DateTimeOffset.Now;
            path = UniquePath(folder, $"{SafeFileName(name)}_{startedAt:yyyy-MM-dd_HH-mm-ss}");

            // FileShare.Read: es pot escoltar o analitzar mentre s'enregistra.
            file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 64 * 1024);
            encoder = new LameEncoder(input.SampleRate, BitrateKbps);
            WriteMarker(path, startedAt, input);

            var ring = new SampleRingBuffer(input.SampleRate * input.Channels * BufferSeconds);
            _zeros = new float[input.SampleRate * input.Channels / 10];
            Interlocked.Exchange(ref _framesCaptured, 0);
            Interlocked.Exchange(ref _samplesLost, 0);
            Interlocked.Exchange(ref _fileSize, 0);
            Volatile.Write(ref _captureGlitches, 0);
            _encoderError = null;
            _stopEncoder = false;

            FilePath = path;
            SampleRate = input.SampleRate;
            StartedAt = startedAt;

            var encoderForThread = encoder;
            var fileForThread = file;
            var limiter = new DynamicsProcessor(input.SampleRate, _limiterSettings);
            _limiter = limiter;
            _encoderThread = new Thread(() => EncoderLoop(ring, encoderForThread, fileForThread, path, limiter))
            {
                IsBackground = true, // el motor atura els enregistradors en tancar-se (Dispose)
                Name = $"Enregistrador {Id}: codificació",
                Priority = ThreadPriority.AboveNormal,
            };

            _encoderThread.Start();
            // A partir d'aquí, el que arriba de l'entrada també va al fitxer.
            _ring = ring;
            if (openedNow) input.Start();
            SetState(RecorderState.Recording);
        }
        catch (Exception ex)
        {
            // No ha arribat a començar (per qualsevol motiu): es desfà tot el que s'hagi creat.
            // Si l'entrada ja era oberta per al mesurador, es deixa oberta.
            _ring = null;
            _limiter = null;
            if (openedNow) CloseInput();
            _stopEncoder = true;
            _encoderThread?.Join(TimeSpan.FromSeconds(2));
            _encoderThread = null;
            encoder?.Dispose();
            file?.Dispose();
            if (path is not null)
            {
                TryDelete(path);
                TryDelete(path + ".rec");
            }
            string message = ex switch
            {
                AudioDeviceException or AudioFileException => ex.Message,
                UnauthorizedAccessException => $"No es pot escriure a la carpeta {folder}: no hi ha permís.",
                IOException => $"No es pot crear el fitxer a la carpeta {folder}.",
                _ => "No s'ha pogut començar a enregistrar.",
            };
            Fail(message, ex.InnerException?.Message ?? ex.Message);
        }
    }

    /// <summary>
    /// Fil de captura de Windows: copia les mostres a la memòria i compta. Res més.
    /// </summary>
    private void OnCaptured(ReadOnlySpan<float> samples, bool silent, bool discontinuity)
    {
        // El mesurador funciona sempre, s'enregistri o no.
        int channels = _inputChannels;
        // El mesurador ja mostra el nivell amb el volum d'enregistrament i el limitador aplicats (el que queda al fitxer).
        if (!silent) Meter.AddInterleaved(samples, channels, _gain, _meterCeiling);

        var ring = _ring;
        if (ring is null) return; // no s'enregistra

        // Només fotogrames sencers (tots els canals d'un instant): si la memòria s'omplís,
        // mai no hi pot quedar mig fotograma, perquè els canals es desordenarien.
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
        // Windows marca sempre el primer paquet després d'arrencar com a discontinuïtat: no és un tall.
        if (discontinuity && Interlocked.Read(ref _framesCaptured) > 0)
            Interlocked.Increment(ref _captureGlitches);

        Interlocked.Add(ref _framesCaptured, samples.Length / channels);
    }

    /// <summary>
    /// Fil de codificació: memòria → estèreo → volum → limitador → LAME → fitxer. Bolcat físic cada 2 s.
    /// En acabar (o si hi ha un error) tanca el fitxer; si tot ha anat bé, esborra el marcador.
    /// </summary>
    private void EncoderLoop(SampleRingBuffer ring, LameEncoder encoder, FileStream file, string path, DynamicsProcessor limiter)
    {
        int channels = _inputChannels;
        var input = new float[ChunkFrames * channels];
        var stereo = new float[ChunkFrames * 2];
        var sinceFlush = Stopwatch.StartNew();
        float appliedGain = _gain;
        var appliedLimiter = limiter.Settings;
        bool ok = false;

        // Volum, limitador i, per acabar, el que encara passi de ±1 es retalla (com sempre).
        void Process(int frames)
        {
            var settings = _limiterSettings;
            if (!ReferenceEquals(settings, appliedLimiter))
            {
                limiter.Settings = settings;
                appliedLimiter = settings;
            }
            appliedGain = ApplyGain(stereo, frames, appliedGain, _gain);
            limiter.Process(stereo.AsSpan(0, frames * 2));
            for (int i = 0; i < frames * 2; i++) stereo[i] = Math.Clamp(stereo[i], -1f, 1f);
        }

        try
        {
            while (true)
            {
                int read = ring.Read(input.AsSpan(0, input.Length - input.Length % channels));
                if (read > 0)
                {
                    int frames = read / channels;
                    ToStereo(input, frames, channels, stereo);
                    Process(frames);
                    var mp3 = encoder.Encode(stereo, frames * 2);
                    file.Write(mp3);
                    Interlocked.Exchange(ref _fileSize, file.Position);
                }
                else if (_stopEncoder)
                {
                    break; // aturat i memòria buida
                }
                else
                {
                    Thread.Sleep(10);
                }

                if (sinceFlush.Elapsed >= FlushInterval)
                {
                    file.Flush(flushToDisk: true);
                    sinceFlush.Restart();
                }
            }

            // El limitador encara té dins seu l'últim 1,5 ms de so: s'hi passa silenci perquè surti.
            int pending = limiter.LatencyFrames;
            if (pending > 0)
            {
                Array.Clear(stereo, 0, pending * 2);
                Process(pending);
                file.Write(encoder.Encode(stereo, pending * 2));
            }

            file.Write(encoder.Flush());
            file.Flush(flushToDisk: true);
            Interlocked.Exchange(ref _fileSize, file.Position);
            ok = true;
        }
        catch (Exception ex)
        {
            _encoderError = ex;
        }
        finally
        {
            try { file.Dispose(); } catch { /* disc ple o perdut: el marcador queda per recuperar-lo */ }
            encoder.Dispose();
            if (ok) TryDelete(path + ".rec");
        }
    }

    /// <summary>Converteix a estèreo: mono es duplica; més de 2 canals, els dos primers.</summary>
    private static void ToStereo(float[] input, int frames, int channels, float[] stereo)
    {
        if (channels == 2)
        {
            Array.Copy(input, stereo, frames * 2);
            return;
        }
        for (int i = 0; i < frames; i++)
        {
            float left = input[i * channels];
            stereo[i * 2] = left;
            stereo[i * 2 + 1] = channels == 1 ? left : input[i * channels + 1];
        }
    }

    /// <summary>
    /// Aplica el volum d'enregistrament a un bloc estèreo. Si ha canviat, passa del valor
    /// anterior al nou al llarg del bloc (rampa: sense clics). Aquí no es retalla res: ho fa
    /// el limitador (si és activat) i, després, es retalla el que encara passi de ±1 (el
    /// mesurador ja ho mostra com a saturació). Retorna el guany aplicat al final del bloc.
    /// </summary>
    private static float ApplyGain(float[] stereo, int frames, float from, float to)
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

    /// <summary>
    /// Deixa d'enviar l'entrada al fitxer, espera que el fil de codificació buidi la memòria
    /// i tanqui el fitxer. L'entrada continua oberta per al mesurador (si no s'ha perdut).
    /// Retorna true si el fitxer s'ha tancat correctament (i el marcador s'ha esborrat).
    /// </summary>
    private bool FinishRecording()
    {
        _ring = null;
        // El fil de captura pot estar acabant d'escriure l'últim paquet: se li deixa temps.
        Thread.Sleep(30);

        _stopEncoder = true;
        _encoderThread?.Join(TimeSpan.FromSeconds(60)); // buidar 10 s de memòria triga molt menys
        _encoderThread = null;
        _limiter = null;

        if (_inputError is not null)
            CloseInput();

        return _encoderError is null;
    }

    /// <summary>Obre l'entrada per al mesurador, si té dispositiu i no ho està. Si falla, ho tornarà a provar.</summary>
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
            // Dispositiu desconnectat, desactivat...: no és un error de l'enregistrador.
            // Tick() ho tornarà a provar d'aquí a una estona.
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
        // Una entrada antiga (ja substituïda) no pot marcar error a la nova.
        input.Stopped += error => { if (error is not null && ReferenceEquals(input, _input)) _inputError = error; };
    }

    /// <summary>Tanca l'entrada (si n'hi ha) i apaga el mesurador.</summary>
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

    private void WriteMarker(string path, DateTimeOffset startedAt, IAudioInput input)
    {
        var marker = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["file"] = Path.GetFileName(path),
            ["startedAt"] = startedAt.ToString("O"),
            ["recorder"] = Id,
            ["deviceId"] = DeviceId,
            ["sampleRate"] = input.SampleRate,
            ["channels"] = 2,
            ["bitrateKbps"] = BitrateKbps,
            ["encoder"] = "LAME CBR, sense etiqueta Xing, sense bit reservoir",
        };
        using var stream = new FileStream(path + ".rec", FileMode.CreateNew, FileAccess.Write);
        stream.Write(System.Text.Encoding.UTF8.GetBytes(marker.ToJsonString(new() { WriteIndented = true })));
        stream.Flush(flushToDisk: true);
    }

    private static string UniquePath(string folder, string baseName)
    {
        string path = Path.Combine(folder, baseName + ".mp3");
        for (int i = 2; File.Exists(path) || File.Exists(path + ".rec"); i++)
            path = Path.Combine(folder, $"{baseName}_{i}.mp3");
        return path;
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return safe.Length == 0 ? "Enregistrament" : safe;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* no és greu */ }
    }

    /// <summary>Dades de l'enregistrament que s'acaba de tancar (dins de _sync).</summary>
    private FinishedRecording? Snapshot(bool ok) => FilePath is null ? null : new FinishedRecording(
        Id, FilePath, StartedAt ?? DateTimeOffset.Now, Elapsed, FileSize, ClosedCleanly: ok);

    private void RaiseFinished(FinishedRecording? finished)
    {
        if (finished is not null) Finished?.Invoke(finished);
    }

    private void Fail(string message, string? detail)
    {
        ErrorMessage = message;
        ErrorDetail = detail;
        SetState(RecorderState.Error);
    }

    private void SetState(RecorderState state)
    {
        State = state;
        _changed = true;
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
}
