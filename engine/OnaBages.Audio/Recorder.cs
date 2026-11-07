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
/// <param name="ArchivePath">Còpia d'historial (D-086), només si s'ha fet sencera; null si no n'hi ha o ha fallat (pas 14b).</param>
public sealed record FinishedRecording(
    string RecorderId, string Path, DateTimeOffset StartedAt, TimeSpan Duration, long FileSize, bool ClosedCleanly,
    string? ArchivePath = null);

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
///
/// Còpia d'historial (fase 5, pas 10a; D-086): si hi ha <see cref="ArchiveFolder"/>, el fil de codificació
/// escriu els mateixos bytes també a <c>carpetaAAAA-MM-DDitxer.mp3</c>. Ningú més no la toca (desar,
/// descartar o l'editor treballen amb el fitxer de treball). Un error a la còpia no afecta mai l'enregistrament.
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
    // --- Alarma de silenci (fase 5, pas 25a; D-121) ---

    public const double DefaultSilenceThresholdDb = -50;
    public const double MinSilenceThresholdDb = -70, MaxSilenceThresholdDb = -30;
    public const int DefaultSilenceSeconds = 30;
    public const int MinSilenceSeconds = 5, MaxSilenceSeconds = 600;

    // Llindar lineal del silenci (el llegeix el fil de codificació).
    private volatile float _silenceThreshold = (float)Math.Pow(10, DefaultSilenceThresholdDb / 20);
    // Fotogrames seguits de silenci al final del que s'ha escrit (l'escriu el fil de codificació).
    private long _silentFrames;
    // Còpia d'historial de l'enregistrament en curs (l'escriu el fil de codificació). Null si no n'hi ha.
    private volatile ArchiveCopy? _archive;
    // Marques de l'enregistrament en curs o del pendent (pas 23a). Dins de _sync.
    private readonly List<RecordingMark> _marks = [];
    // Perquè dues marques seguides no escriguin el fitxer de marques alhora (i l'última escriptura sigui la completa).
    private readonly Lock _marksFile = new();

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

    /// <summary>Carpeta de l'historial d'enregistraments (còpia de cada original), o null si no se'n fa còpia.</summary>
    public string? ArchiveFolder { get; private set; }

    /// <summary>Còpia d'historial de l'enregistrament en curs o de l'últim (null si no n'hi ha).</summary>
    public string? ArchivePath { get; private set; }

    /// <summary>
    /// Per què no hi ha (o s'ha interromput) la còpia d'historial de l'enregistrament en curs o de l'últim.
    /// L'enregistrament principal continua igualment.
    /// </summary>
    public string? ArchiveError { get; private set; }

    /// <summary>Detall tècnic de <see cref="ArchiveError"/> (la carpeta i el missatge de Windows), o null.</summary>
    public string? ArchiveErrorDetail { get; private set; }

    /// <summary>
    /// Carpeta de l'historial d'enregistraments (null = sense còpia). S'aplica a partir del pròxim enregistrament.
    /// </summary>
    public void SetArchive(string? folder)
    {
        lock (_sync)
        {
            if (folder is not null && !Path.IsPathFullyQualified(folder))
                throw new RecorderCommandException("La carpeta de l'historial ha de ser una ruta completa (p. ex. E:\\Historial).");
            ArchiveFolder = folder;
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

    /// <summary>Si l'alarma de silenci vigila aquest enregistrador (per defecte, sí).</summary>
    public bool SilenceAlarmEnabled { get; private set; } = true;

    /// <summary>Per sota d'aquest nivell (dBFS, als dos canals) es considera silenci.</summary>
    public double SilenceThresholdDb { get; private set; } = DefaultSilenceThresholdDb;

    /// <summary>Quants segons seguits de silenci fan saltar l'alarma.</summary>
    public int SilenceSeconds { get; private set; } = DefaultSilenceSeconds;

    /// <summary>
    /// L'alarma ha saltat: mentre s'enregistra, fa com a mínim <see cref="SilenceSeconds"/> que el que entra al fitxer
    /// és silenci. S'apaga sola quan torna el so o en aturar. Ho actualitza <see cref="Tick"/>.
    /// </summary>
    public bool SilenceAlarm { get; private set; }

    /// <summary>Segons seguits de silenci al final del que s'ha enregistrat fins ara (0 si no s'enregistra).</summary>
    public double SilentSeconds => State == RecorderState.Recording && SampleRate > 0
        ? Interlocked.Read(ref _silentFrames) / (double)SampleRate
        : 0;

    /// <summary>
    /// Ajustos de l'alarma de silenci (comuns a tots els enregistradors, del perfil). S'apliquen de seguida,
    /// també a mig enregistrament. Els valors fora de marge es porten al límit.
    /// </summary>
    public void SetSilenceAlarm(bool enabled, double thresholdDb, int seconds)
    {
        lock (_sync)
        {
            SilenceAlarmEnabled = enabled;
            SilenceThresholdDb = Math.Round(Math.Clamp(double.IsFinite(thresholdDb) ? thresholdDb : DefaultSilenceThresholdDb,
                MinSilenceThresholdDb, MaxSilenceThresholdDb));
            SilenceSeconds = Math.Clamp(seconds, MinSilenceSeconds, MaxSilenceSeconds);
            _silenceThreshold = (float)Math.Pow(10, SilenceThresholdDb / 20);
            UpdateSilenceAlarm();
            _changed = true;
        }
        RaiseStateChanged();
    }

    // Dins de _sync. Encén o apaga l'alarma segons el silenci que s'ha comptat.
    private void UpdateSilenceAlarm()
    {
        bool alarm = SilenceAlarmEnabled && State == RecorderState.Recording && SilentSeconds >= SilenceSeconds;
        if (alarm == SilenceAlarm) return;
        SilenceAlarm = alarm;
        _changed = true;
    }

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

    /// <summary>Marques de l'enregistrament en curs o del pendent (pas 23a), per ordre. Còpia.</summary>
    public IReadOnlyList<RecordingMark> Marks
    {
        get { lock (_sync) return [.. _marks]; }
    }

    /// <summary>
    /// Posa una marca a la posició d'ara de l'enregistrament (pas 23a; D-110) i desa totes les marques al fitxer
    /// <c>Nom.mp3.marques</c> (i al costat de la còpia de l'historial, si n'hi ha). Només mentre s'enregistra.
    /// L'enregistrament no es toca mai: si el fitxer de marques no es pot escriure, la marca es queda a la memòria
    /// (es tornarà a provar amb la marca següent) i es diu.
    /// </summary>
    public RecordingMark Mark()
    {
        RecordingMark mark;
        lock (_sync)
        {
            if (State != RecorderState.Recording)
                throw new RecorderCommandException("Només es poden posar marques mentre s'enregistra.");
            mark = new RecordingMark(Elapsed.TotalSeconds);
            _marks.Add(mark);
            _changed = true;
        }
        RaiseStateChanged();
        WriteMarksFile("La marca s'ha posat, però no s'ha pogut desar al disc");
        return mark;
    }

    /// <summary>Llargada màxima del nom d'una marca (pas 23e).</summary>
    public const int MaxMarkNameLength = 60;

    /// <summary>
    /// Posa (o canvia) el nom de la marca <paramref name="number"/> (1, 2, 3...; fase 5, pas 23e; D-114), mentre s'enregistra
    /// o quan és pendent. Un nom buit la deixa sense nom. Com a molt <see cref="MaxMarkNameLength"/> caràcters. Es desa
    /// de seguida al fitxer de marques.
    /// </summary>
    public void NameMark(int number, string? name)
    {
        name = CleanMarkName(name);
        lock (_sync)
        {
            if (State is not (RecorderState.Recording or RecorderState.Pending))
                throw new RecorderCommandException("Aquest enregistrador no té cap enregistrament amb marques.");
            if (number < 1 || number > _marks.Count)
                throw new RecorderCommandException($"No hi ha cap marca {number}.");
            _marks[number - 1] = _marks[number - 1] with { Name = name };
            _changed = true;
        }
        RaiseStateChanged();
        WriteMarksFile("El nom s'ha posat, però no s'ha pogut desar al disc");
    }

    /// <summary>
    /// Mou la marca <paramref name="number"/> a <paramref name="seconds"/> de l'enregistrament (fase 5, pas 24b; D-118), mentre
    /// s'enregistra o quan és pendent: des de l'editor, que no pot escriure el fitxer de marques mentre l'enregistrador n'és
    /// l'amo (el reescriuria amb la marca següent). Com a molt fins on s'ha enregistrat. Les marques queden <b>ordenades per
    /// temps</b> (si passa per davant d'una altra, es renumeren), igual que quan es llegeixen del fitxer: així el número és el
    /// mateix a l'enregistrador i a l'editor.
    /// </summary>
    public void MoveMark(int number, double seconds)
    {
        lock (_sync)
        {
            if (State is not (RecorderState.Recording or RecorderState.Pending))
                throw new RecorderCommandException("Aquest enregistrador no té cap enregistrament amb marques.");
            if (number < 1 || number > _marks.Count)
                throw new RecorderCommandException($"No hi ha cap marca {number}.");
            _marks[number - 1] = _marks[number - 1] with { Seconds = Math.Clamp(seconds, 0, Elapsed.TotalSeconds) };
            _marks.Sort((a, b) => a.Seconds.CompareTo(b.Seconds));
            _changed = true;
        }
        RaiseStateChanged();
        WriteMarksFile("La marca s'ha mogut, però no s'ha pogut desar al disc");
    }

    /// <summary>
    /// Afegeix una marca a <paramref name="seconds"/> de l'enregistrament (fase 5, pas 27a; D-125), mentre s'enregistra o
    /// quan és pendent: des de l'editor (com <see cref="MoveMark"/>). Com a molt fins on s'ha enregistrat. Les marques queden
    /// ordenades per temps (les de després es renumeren). Retorna el número de la marca nova.
    /// </summary>
    public int AddMark(double seconds, string? name = null)
    {
        name = CleanMarkName(name);
        int number;
        lock (_sync)
        {
            if (State is not (RecorderState.Recording or RecorderState.Pending))
                throw new RecorderCommandException("Aquest enregistrador no té cap enregistrament on posar marques.");
            var mark = new RecordingMark(Math.Clamp(double.IsFinite(seconds) ? seconds : 0, 0, Elapsed.TotalSeconds), name);
            number = _marks.FindLastIndex(m => m.Seconds <= mark.Seconds) + 2; // darrere de les que són al mateix punt
            _marks.Insert(number - 1, mark);
            _changed = true;
        }
        RaiseStateChanged();
        WriteMarksFile("La marca s'ha posat, però no s'ha pogut desar al disc");
        return number;
    }

    /// <summary>Esborra la marca <paramref name="number"/> (pas 27a; D-125), mentre s'enregistra o quan és pendent. Les de després es renumeren.</summary>
    public void RemoveMark(int number)
    {
        lock (_sync)
        {
            if (State is not (RecorderState.Recording or RecorderState.Pending))
                throw new RecorderCommandException("Aquest enregistrador no té cap enregistrament amb marques.");
            if (number < 1 || number > _marks.Count)
                throw new RecorderCommandException($"No hi ha cap marca {number}.");
            _marks.RemoveAt(number - 1);
            _changed = true;
        }
        RaiseStateChanged();
        WriteMarksFile("La marca s'ha esborrat, però no s'ha pogut desar al disc");
    }

    /// <summary>Nom d'una marca net: sense espais als extrems, buit = sense nom. Massa llarg: error.</summary>
    public static string? CleanMarkName(string? name)
    {
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (name is { Length: > MaxMarkNameLength })
            throw new RecorderCommandException($"El nom de la marca pot tenir com a molt {MaxMarkNameLength} caràcters.");
        return name;
    }

    /// <summary>
    /// Desa totes les marques al fitxer de marques (i al de la còpia de l'historial). Fora de _sync; mai des del fil d'àudio.
    /// Si no es pot, les marques es queden a la memòria (es tornarà a provar la vegada següent) i es diu amb <paramref name="failure"/>.
    /// </summary>
    private void WriteMarksFile(string failure)
    {
        lock (_marksFile)
        {
            string? path, archive;
            List<RecordingMark> marks;
            lock (_sync)
            {
                path = FilePath;
                archive = ArchiveError is null ? ArchivePath : null;
                marks = [.. _marks];
            }
            if (path is null) return;
            try
            {
                RecordingMarks.Write(path, marks);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new RecorderCommandException($"{failure} ({ex.Message}). Es tornarà a provar amb la marca següent.");
            }
            // La còpia de l'historial és secundària: si no es pot, no es diu res.
            if (archive is not null)
            {
                try { RecordingMarks.Write(archive, marks); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
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
            _marks.Clear(); // ja són al fitxer de marques
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
            // La còpia d'historial ha fallat: es diu (l'enregistrament continua).
            if (ArchiveError is null && _archive?.Error is { } archiveError)
            {
                SetArchiveFailed(archiveError);
                _changed = true;
            }

            UpdateSilenceAlarm();

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
        ArchiveCopy? archive = null;
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
            archive = OpenArchive(path, startedAt);

            var ring = new SampleRingBuffer(input.SampleRate * input.Channels * BufferSeconds);
            _zeros = new float[input.SampleRate * input.Channels / 10];
            Interlocked.Exchange(ref _framesCaptured, 0);
            Interlocked.Exchange(ref _samplesLost, 0);
            Interlocked.Exchange(ref _fileSize, 0);
            Interlocked.Exchange(ref _silentFrames, 0);
            SilenceAlarm = false;
            Volatile.Write(ref _captureGlitches, 0);
            _encoderError = null;
            _stopEncoder = false;

            FilePath = path;
            SampleRate = input.SampleRate;
            StartedAt = startedAt;
            _marks.Clear();

            var encoderForThread = encoder;
            var fileForThread = file;
            var limiter = new DynamicsProcessor(input.SampleRate, _limiterSettings);
            _limiter = limiter;
            _archive = archive;
            var archiveForThread = archive;
            _encoderThread = new Thread(() => EncoderLoop(ring, encoderForThread, fileForThread, path, limiter, archiveForThread))
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
            _archive = null;
            archive?.Close();
            if (archive is not null && ArchivePath is not null) TryDelete(ArchivePath);
            ArchivePath = null;
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
    private void EncoderLoop(SampleRingBuffer ring, LameEncoder encoder, FileStream file, string path, DynamicsProcessor limiter, ArchiveCopy? archive)
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
                    CountSilence(stereo, frames);
                    var mp3 = encoder.Encode(stereo, frames * 2);
                    file.Write(mp3);
                    archive?.Write(mp3);
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
                    archive?.Flush();
                    sinceFlush.Restart();
                }
            }

            // El limitador encara té dins seu l'últim 1,5 ms de so: s'hi passa silenci perquè surti.
            int pending = limiter.LatencyFrames;
            if (pending > 0)
            {
                Array.Clear(stereo, 0, pending * 2);
                Process(pending);
                var tail = encoder.Encode(stereo, pending * 2);
                file.Write(tail);
                archive?.Write(tail);
            }

            var last = encoder.Flush();
            file.Write(last);
            archive?.Write(last);
            file.Flush(flushToDisk: true);
            archive?.Flush();
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
            archive?.Close();
            encoder.Dispose();
            if (ok) TryDelete(path + ".rec");
        }
    }

    /// <summary>
    /// Alarma de silenci: si tot el bloc (el que va al fitxer, amb el volum i el limitador) és per sota del llindar
    /// als dos canals, s'afegeix al silenci seguit; si hi ha so, es torna a zero. Fil de codificació, sense objectes nous.
    /// </summary>
    private void CountSilence(float[] stereo, int frames)
    {
        float threshold = _silenceThreshold;
        for (int i = 0; i < frames * 2; i++)
        {
            if (Math.Abs(stereo[i]) >= threshold)
            {
                Interlocked.Exchange(ref _silentFrames, 0);
                return;
            }
        }
        Interlocked.Add(ref _silentFrames, frames);
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
        if (ArchiveError is null && _archive?.Error is { } archiveError)
            SetArchiveFailed(archiveError);
        _archive = null;

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

    /// <summary>
    /// Crea la còpia d'historial (carpeta del dia, el mateix nom que el fitxer de treball). Si no es pot,
    /// l'enregistrament comença igualment i es diu per què (<see cref="ArchiveError"/>).
    /// </summary>
    private ArchiveCopy? OpenArchive(string path, DateTimeOffset startedAt)
    {
        ArchivePath = null;
        ArchiveError = null;
        ArchiveErrorDetail = null;
        if (ArchiveFolder is not { } root) return null;
        try
        {
            string folder = Path.Combine(root, startedAt.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(folder);
            string copyPath = UniquePath(folder, Path.GetFileNameWithoutExtension(path));
            var stream = new FileStream(copyPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 64 * 1024);
            ArchivePath = copyPath;
            return new ArchiveCopy(stream);
        }
        catch (Exception ex)
        {
            ArchiveError = "No es fa la còpia d'historial: la carpeta no està disponible. L'enregistrament continua.";
            ArchiveErrorDetail = $"{root}: {ex.Message}";
            return null;
        }
    }

    private void SetArchiveFailed(Exception ex)
    {
        ArchiveError = "La còpia d'historial s'ha interromput (disc ple o no disponible). L'enregistrament continua.";
        ArchiveErrorDetail = $"{ArchivePath}: {ex.Message}";
    }

    /// <summary>
    /// Segon fitxer de l'enregistrament (historial). Només l'escriu el fil de codificació. Si falla, apunta
    /// l'error, es tanca i ja no fa res més: mai no llança cap excepció cap a l'enregistrament principal.
    /// </summary>
    private sealed class ArchiveCopy(FileStream file)
    {
        private FileStream? _file = file;
        private volatile Exception? _error;

        /// <summary>Per què s'ha deixat d'escriure, o null.</summary>
        public Exception? Error => _error;

        public void Write(ReadOnlySpan<byte> data)
        {
            if (_file is null) return;
            try { _file.Write(data); }
            catch (Exception ex) { Fail(ex); }
        }

        /// <summary>Bolcat físic al disc (com el del fitxer de treball).</summary>
        public void Flush()
        {
            if (_file is null) return;
            try { _file.Flush(flushToDisk: true); }
            catch (Exception ex) { Fail(ex); }
        }

        public void Close()
        {
            var stream = _file;
            _file = null;
            try { stream?.Dispose(); }
            catch (Exception ex) { _error ??= ex; }
        }

        private void Fail(Exception ex)
        {
            _error = ex;
            Close();
        }
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
        Id, FilePath, StartedAt ?? DateTimeOffset.Now, Elapsed, FileSize, ClosedCleanly: ok,
        ArchivePath: ArchiveError is null ? ArchivePath : null);

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
        if (state != RecorderState.Recording) SilenceAlarm = false; // sense enregistrar no hi ha alarma
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
