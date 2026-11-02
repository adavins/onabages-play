using System.Diagnostics;
using System.Text.Json.Nodes;

namespace OnaBages.Audio;

/// <summary>Estats de l'enregistrador (especificació, 8.3). Fase 1: sense Desar/Descartar/Editar.</summary>
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

    private IAudioInput? _input;
    private SampleRingBuffer? _ring;
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

    /// <param name="id">Identificador ("1", "2"...).</param>
    /// <param name="openInput">Obre una entrada per identificador de dispositiu. Per defecte, WASAPI.</param>
    public Recorder(string id, Func<string, IAudioInput>? openInput = null)
    {
        Id = id;
        _openInput = openInput ?? (deviceId => WasapiAudioInput.Open(deviceId));
    }

    public string Id { get; }
    public RecorderState State { get; private set; } = RecorderState.Idle;
    public string? DeviceId { get; private set; }

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
                string? root = Path.GetPathRoot(FilePath ?? DefaultFolder);
                return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
            }
            catch
            {
                return null;
            }
        }
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
            DeviceId = deviceId;
            _changed = true;
        }
        RaiseStateChanged();
    }

    /// <summary>
    /// Comença un enregistrament nou. Es pot fer des d'Inactiu, Pendent o Error
    /// (l'enregistrament anterior es queda on és).
    /// </summary>
    /// <param name="folder">Carpeta de treball. Per defecte, <see cref="DefaultFolder"/>.</param>
    /// <param name="name">Nom per al fitxer (p. ex. «Programa»). Per defecte, «Enregistrador N».</param>
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
            BeginRecording(folder ?? DefaultFolder, name ?? $"Enregistrador {Id}");
        }
        RaiseStateChanged();
    }

    /// <summary>Atura l'enregistrament, tanca el fitxer (vàlid) i passa a Pendent.</summary>
    public void Stop()
    {
        lock (_sync)
        {
            if (State != RecorderState.Recording)
                throw new RecorderCommandException("No s'està enregistrant.");

            bool ok = FinishRecording();
            if (ok)
                SetState(RecorderState.Pending);
            else
                Fail("No s'ha pogut tancar bé el fitxer. Es podrà recuperar en tornar a arrencar.", _encoderError?.Message);
        }
        RaiseStateChanged();
    }

    /// <summary>
    /// Revisió periòdica, fora dels fils d'àudio: detecta si l'entrada s'ha perdut
    /// o si la codificació ha fallat (p. ex. disc ple).
    /// </summary>
    public void Tick()
    {
        lock (_sync)
        {
            if (State != RecorderState.Recording) return;

            if (_inputError is { } inputError)
            {
                // Especificació 5.3: es tanca el fitxer de manera neta i s'avisa.
                bool ok = FinishRecording();
                Fail(ok
                    ? "Entrada no disponible: s'ha desconnectat. L'enregistrament s'ha desat fins aquí; pots començar-ne un de nou."
                    : "Entrada no disponible i no s'ha pogut tancar bé el fitxer. Es podrà recuperar en tornar a arrencar.",
                    inputError.Message);
            }
            else if (_encoderError is { } encoderError)
            {
                FinishRecording();
                Fail("No es pot escriure el fitxer (disc ple o inaccessible). Es podrà recuperar en tornar a arrencar.",
                    encoderError.Message);
            }
        }
        RaiseStateChanged();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (State == RecorderState.Recording)
                FinishRecording();
        }
    }

    // -----------------------------------------------------------------------
    // Interns (dins de _sync)
    // -----------------------------------------------------------------------

    private void BeginRecording(string folder, string name)
    {
        IAudioInput? input = null;
        FileStream? file = null;
        LameEncoder? encoder = null;
        string? path = null;
        try
        {
            input = _openInput(DeviceId!);

            Directory.CreateDirectory(folder);
            var startedAt = DateTimeOffset.Now;
            path = UniquePath(folder, $"{SafeFileName(name)}_{startedAt:yyyy-MM-dd_HH-mm-ss}");

            // FileShare.Read: es pot escoltar o analitzar mentre s'enregistra.
            file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 64 * 1024);
            encoder = new LameEncoder(input.SampleRate, BitrateKbps);
            WriteMarker(path, startedAt, input);

            _input = input;
            _inputChannels = input.Channels;
            _ring = new SampleRingBuffer(input.SampleRate * input.Channels * BufferSeconds);
            _zeros = new float[input.SampleRate * input.Channels / 10];
            Interlocked.Exchange(ref _framesCaptured, 0);
            Interlocked.Exchange(ref _samplesLost, 0);
            Interlocked.Exchange(ref _fileSize, 0);
            Volatile.Write(ref _captureGlitches, 0);
            _inputError = null;
            _encoderError = null;
            _stopEncoder = false;

            FilePath = path;
            SampleRate = input.SampleRate;
            StartedAt = startedAt;

            var ring = _ring;
            var encoderForThread = encoder;
            var fileForThread = file;
            _encoderThread = new Thread(() => EncoderLoop(ring, encoderForThread, fileForThread, path))
            {
                IsBackground = true, // el motor atura els enregistradors en tancar-se (Dispose)
                Name = $"Enregistrador {Id}: codificació",
                Priority = ThreadPriority.AboveNormal,
            };

            input.DataAvailable += OnCaptured;
            input.Stopped += error => { if (error is not null) _inputError = error; };

            _encoderThread.Start();
            input.Start();
            SetState(RecorderState.Recording);
        }
        catch (Exception ex)
        {
            // No ha arribat a començar (per qualsevol motiu): es desfà tot el que s'hagi creat.
            if (input is not null) input.DataAvailable -= OnCaptured;
            try { input?.Stop(); } catch { /* potser no havia arrencat */ }
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
            input?.Dispose();
            _input = null;

            _ring = null;
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
        var ring = _ring;
        if (ring is null) return;

        // Només fotogrames sencers (tots els canals d'un instant): si la memòria s'omplís,
        // mai no hi pot quedar mig fotograma, perquè els canals es desordenarien.
        int channels = _inputChannels;
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
    /// Fil de codificació: memòria → estèreo → LAME → fitxer. Bolcat físic cada 2 s.
    /// En acabar (o si hi ha un error) tanca el fitxer; si tot ha anat bé, esborra el marcador.
    /// </summary>
    private void EncoderLoop(SampleRingBuffer ring, LameEncoder encoder, FileStream file, string path)
    {
        int channels = _inputChannels;
        var input = new float[ChunkFrames * channels];
        var stereo = new float[ChunkFrames * 2];
        var sinceFlush = Stopwatch.StartNew();
        bool ok = false;

        try
        {
            while (true)
            {
                int read = ring.Read(input.AsSpan(0, input.Length - input.Length % channels));
                if (read > 0)
                {
                    int frames = read / channels;
                    ToStereo(input, frames, channels, stereo);
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
    /// Atura la captura, espera que el fil de codificació buidi la memòria i tanqui el fitxer.
    /// Retorna true si el fitxer s'ha tancat correctament (i el marcador s'ha esborrat).
    /// </summary>
    private bool FinishRecording()
    {
        try { _input?.Stop(); } catch { /* entrada ja perduda */ }
        if (_input is not null) _input.DataAvailable -= OnCaptured;

        _stopEncoder = true;
        _encoderThread?.Join(TimeSpan.FromSeconds(60)); // buidar 10 s de memòria triga molt menys
        _encoderThread = null;

        try { _input?.Dispose(); } catch { /* idem */ }
        _input = null;
        _ring = null;

        return _encoderError is null;
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
