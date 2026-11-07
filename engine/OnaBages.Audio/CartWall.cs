using System.Runtime.InteropServices;
using NAudio.Wave;

namespace OnaBages.Audio;

/// <summary>Estat d'un botó de la botonera.</summary>
public enum CartButtonStatus
{
    /// <summary>Encara s'està llegint l'inici del fitxer (es pot fer sonar igualment, amb una mica de retard).</summary>
    Loading,
    /// <summary>A punt: l'inici ja és a la memòria i sona a l'instant.</summary>
    Ready,
    /// <summary>El fitxer no es troba.</summary>
    Missing,
    /// <summary>El fitxer no es pot llegir.</summary>
    Error,
}

/// <summary>Un botó assignat de la botonera. Només es canvia dins de la botonera (amb el seu bloqueig).</summary>
public sealed class CartButton
{
    internal CartButton(int page, int index, string path, string name)
    {
        Page = page;
        Index = index;
        Path = path;
        Name = name;
    }

    /// <summary>Pàgina (0, 1, 2...).</summary>
    public int Page { get; }

    /// <summary>Posició dins de la pàgina (0, 1, 2..., per files).</summary>
    public int Index { get; }

    public string Path { get; internal set; }
    public string Name { get; internal set; }

    /// <summary>Color del botó (p. ex. "#d63384"), o null per al de per defecte.</summary>
    public string? Color { get; internal set; }

    /// <summary>Volum del botó, de −30 a +10 dB.</summary>
    public double GainDb { get; internal set; }

    public CartButtonStatus Status { get; internal set; } = CartButtonStatus.Loading;
    public TimeSpan Duration { get; internal set; }
    public string? ErrorMessage { get; internal set; }

    // Inici del fitxer ja descodificat (estèreo intercalat, a la freqüència de la sortida): sona sense esperar el disc.
    internal float[]? Head;
    internal int HeadRate;
    // L'inici és tot el fitxer (un tall curt): no cal llegir res més.
    internal bool HeadIsWhole;
    // Cada vegada que canvia el fitxer o la freqüència, una lectura nova: les antigues ja no compten.
    internal int LoadVersion;
}

/// <summary>Error d'una ordre de la botonera. El missatge és per a l'operador.</summary>
public sealed class CartCommandException(string message) : Exception(message);

/// <summary>
/// Botonera d'instantanis (fase 5, pas 28a; D-127): botons que fan sonar talls curts a l'instant, diversos alhora,
/// per una sortida pròpia (que pot ser la mateixa que la d'un reproductor: el mode compartit de Windows els barreja).
///
/// Perquè soni a l'instant, de cada botó es guarda a la memòria l'inici ja descodificat (<see cref="HeadSeconds"/>):
/// en prémer-lo, el fil d'àudio el comença de seguida i, mentrestant, un fil de lectura omple la resta. La sortida
/// està sempre oberta (envia silenci quan no sona res). El fil d'àudio barreja les veus que sonen sense crear objectes,
/// sense esperar i sense bloquejos (<see cref="Mixer"/>).
/// </summary>
public sealed class CartWall : IDisposable
{
    /// <summary>Segons de l'inici de cada botó que es guarden descodificats a la memòria.</summary>
    public const double HeadSeconds = 3;

    /// <summary>Pàgines de la botonera, i files i columnes de botons de cada pàgina (D-127: 4 × 4, 4 pàgines).</summary>
    public const int Pages = 4, Rows = 4, Columns = 4;

    public const int ButtonsPerPage = Rows * Columns;

    /// <summary>Llargada màxima del nom d'un botó.</summary>
    public const int MaxNameLength = 40;

    /// <summary>Talls que poden sonar alhora.</summary>
    public const int MaxVoices = 8;

    public const double MinGainDb = -30, MaxGainDb = 10;

    /// <summary>Fos en aturar un botó (perquè no faci clic).</summary>
    public const int StopFadeMs = 15;

    private static readonly TimeSpan OutputRetry = TimeSpan.FromSeconds(3);

    private readonly Func<string, IAudioOutput> _openOutput;
    private readonly Lock _sync = new();
    private readonly Dictionary<(int Page, int Index), CartButton> _buttons = [];
    private Voice[] _voices = [];
    private IAudioOutput? _output;
    private volatile Exception? _outputError;
    private DateTime _nextOutputAttempt;
    private int _rate = 48000;
    private bool _changed;

    /// <param name="openOutput">Obre una sortida per identificador de dispositiu. Per defecte, WASAPI.</param>
    public CartWall(Func<string, IAudioOutput>? openOutput = null)
    {
        _openOutput = openOutput ?? (id => WasapiAudioOutput.Open(id));
        _voices = CreateVoices(_rate);
    }

    /// <summary>Es dispara quan canvia un botó, la sortida, un error o què sona (fora del bloqueig).</summary>
    public event Action<CartWall>? StateChanged;

    public string? DeviceId { get; private set; }

    /// <summary>La sortida és oberta i a punt.</summary>
    public bool OutputReady { get; private set; }

    /// <summary>Per què no funciona la sortida (null si va bé o si no n'hi ha cap de triada).</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>Talls que no han arribat a temps del disc (s'hi ha posat silenci). Ha de ser 0.</summary>
    public int Underruns => _voices.Sum(v => Volatile.Read(ref v.Underruns));

    /// <summary>Pic del que surt (de tots els talls barrejats), per al mesurador.</summary>
    public PeakMeter Meter { get; } = new();

    /// <summary>Els botons assignats (una còpia de la llista).</summary>
    public IReadOnlyList<CartButton> Buttons
    {
        get { lock (_sync) return [.. _buttons.Values.OrderBy(b => b.Page).ThenBy(b => b.Index)]; }
    }

    public CartButton? ButtonAt(int page, int index)
    {
        lock (_sync) return _buttons.GetValueOrDefault((page, index));
    }

    /// <summary>Què sona ara: botó, posició i durada. Un botó pot sonar més d'una vegada alhora.</summary>
    public IReadOnlyList<(CartButton Button, TimeSpan Position)> Playing
    {
        get
        {
            lock (_sync)
            {
                var result = new List<(CartButton, TimeSpan)>();
                foreach (var v in _voices)
                {
                    if (v.InUse && !v.Ended && v.Button is { } b)
                        result.Add((b, TimeSpan.FromSeconds(Volatile.Read(ref v.FramesPlayed) / (double)v.Rate)));
                }
                return result;
            }
        }
    }

    public bool IsPlaying(int page, int index)
    {
        lock (_sync) return _voices.Any(v => v.InUse && !v.Ended && v.Button is { } b && b.Page == page && b.Index == index);
    }

    // -----------------------------------------------------------------------
    // Botons
    // -----------------------------------------------------------------------

    /// <summary>
    /// Assigna un fitxer a un botó (si en tenia un altre, el substitueix; si sonava, s'atura). El nom, per defecte, és el
    /// del fitxer sense l'extensió. L'inici es llegeix en segon pla.
    /// </summary>
    public CartButton Assign(int page, int index, string path, string? name = null)
    {
        if (!System.IO.Path.IsPathFullyQualified(path))
            throw new CartCommandException("La ruta del fitxer ha de ser completa.");
        CartButton button;
        lock (_sync)
        {
            if (_buttons.TryGetValue((page, index), out var old)) StopVoicesOf(old);
            button = new CartButton(page, index, path, CleanName(name) ?? System.IO.Path.GetFileNameWithoutExtension(path));
            _buttons[(page, index)] = button;
            StartLoad(button);
            _changed = true;
        }
        RaiseStateChanged();
        return button;
    }

    /// <summary>Canvia el nom, el color o el volum d'un botó (els que no són null). El volum s'aplica de seguida.</summary>
    public void Configure(int page, int index, string? name = null, string? color = null, double? gainDb = null, bool clearColor = false)
    {
        lock (_sync)
        {
            var button = RequireButton(page, index);
            if (CleanName(name) is { } n) button.Name = n;
            if (clearColor) button.Color = null;
            else if (color is not null) button.Color = color;
            if (gainDb is { } g)
            {
                button.GainDb = Math.Round(Math.Clamp(double.IsFinite(g) ? g : 0, MinGainDb, MaxGainDb), 1);
                float linear = DbToGain(button.GainDb);
                foreach (var v in _voices)
                    if (v.InUse && v.Button == button) v.Gain = linear;
            }
            _changed = true;
        }
        RaiseStateChanged();
    }

    /// <summary>Treu el fitxer d'un botó (si sonava, s'atura). El fitxer no es toca.</summary>
    public void Clear(int page, int index)
    {
        lock (_sync)
        {
            if (!_buttons.Remove((page, index), out var button)) return;
            StopVoicesOf(button);
            button.LoadVersion++;
            _changed = true;
        }
        RaiseStateChanged();
    }

    private CartButton RequireButton(int page, int index) =>
        _buttons.GetValueOrDefault((page, index)) ?? throw new CartCommandException("Aquest botó no té cap fitxer.");

    private static string? CleanName(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    // -----------------------------------------------------------------------
    // Sonar i aturar
    // -----------------------------------------------------------------------

    /// <summary>
    /// Prémer un botó: si no sona, el fa sonar des del principi; si ja sona, l'atura (amb un fos molt curt).
    /// Diversos botons poden sonar alhora.
    /// </summary>
    /// <returns>true si ara sona; false si s'ha aturat.</returns>
    public bool Press(int page, int index)
    {
        bool playing;
        lock (_sync)
        {
            var button = RequireButton(page, index);
            if (_voices.Any(v => v.InUse && !v.Ended && v.Button == button))
            {
                StopVoicesOf(button);
                playing = false;
            }
            else
            {
                StartVoice(button);
                playing = true;
            }
            _changed = true;
        }
        RaiseStateChanged();
        return playing;
    }

    /// <summary>Atura un botó (si no sona, no fa res).</summary>
    public void Stop(int page, int index)
    {
        lock (_sync)
        {
            if (_buttons.GetValueOrDefault((page, index)) is not { } button) return;
            StopVoicesOf(button);
            _changed = true;
        }
        RaiseStateChanged();
    }

    /// <summary>Atura tot el que sona a la botonera.</summary>
    public void StopAll()
    {
        lock (_sync)
        {
            foreach (var v in _voices)
                if (v.InUse && !v.Ended) v.StopRequested = true;
            _changed = true;
        }
        RaiseStateChanged();
    }

    // Dins de _sync.
    private void StartVoice(CartButton button)
    {
        if (!OutputReady)
            throw new CartCommandException(DeviceId is null
                ? "Cal triar abans una sortida per a la botonera."
                : ErrorMessage ?? "La sortida de la botonera no està disponible.");
        if (button.Status is CartButtonStatus.Missing or CartButtonStatus.Error)
            throw new CartCommandException(button.ErrorMessage ?? "No es pot llegir el fitxer d'aquest botó.");

        var voice = _voices.FirstOrDefault(v => !v.InUse)
            ?? throw new CartCommandException($"Ja sonen {MaxVoices} talls alhora: atura'n algun.");

        // L'inici ja és a la memòria (si encara s'està llegint, o és d'una altra freqüència, es llegeix tot del disc).
        float[] head = button.Head is { } h && button.HeadRate == voice.Rate ? h : [];
        bool whole = head.Length > 0 && button.HeadIsWhole;

        voice.Button = button;
        voice.Head = head;
        voice.HeadPos = 0;
        voice.Ring.Clear();
        voice.SourceFinished = whole;
        voice.StopRequested = false;
        voice.StopFadeLeft = voice.Rate * StopFadeMs / 1000;
        voice.Ended = false;
        voice.Gain = DbToGain(button.GainDb);
        Volatile.Write(ref voice.FramesPlayed, 0);
        voice.InUse = true;
        if (!whole)
        {
            voice.ReaderStop = false;
            var startAt = TimeSpan.FromSeconds(head.Length / 2.0 / voice.Rate);
            var v = voice;
            string path = button.Path;
            voice.Reader = new Thread(() => ReaderLoop(v, path, startAt))
            {
                IsBackground = true,
                Name = $"Botonera: lectura de {button.Name}",
                Priority = ThreadPriority.AboveNormal,
            };
            voice.Reader.Start();
        }
        voice.Active = true; // l'últim: a partir d'aquí el fil d'àudio la fa sonar
    }

    // Dins de _sync.
    private void StopVoicesOf(CartButton button)
    {
        foreach (var v in _voices)
            if (v.InUse && !v.Ended && v.Button == button) v.StopRequested = true;
    }

    /// <summary>Fil de lectura d'una veu: omple la memòria amb la resta del fitxer, a partir de l'inici que ja hi ha.</summary>
    private static void ReaderLoop(Voice voice, string path, TimeSpan startAt)
    {
        try
        {
            using var source = AudioFileSource.Open(path, voice.Rate, startAt, exactStart: true);
            var buffer = new float[voice.Rate / 10 * 2]; // 100 ms
            while (!voice.ReaderStop)
            {
                if (voice.Ring.Free < buffer.Length)
                {
                    Thread.Sleep(10);
                    continue;
                }
                int read = source.Read(buffer);
                if (read <= 0) break;
                voice.Ring.Write(buffer.AsSpan(0, read));
            }
        }
        catch (Exception ex)
        {
            voice.ReaderError = ex; // el que ja hi ha a la memòria sona igualment
        }
        finally
        {
            voice.SourceFinished = true;
        }
    }

    // -----------------------------------------------------------------------
    // Sortida
    // -----------------------------------------------------------------------

    /// <summary>Sortida de la botonera. Si sonava alguna cosa, s'atura. La sortida queda oberta (en silenci) per sonar a l'instant.</summary>
    public void SetDevice(string deviceId)
    {
        lock (_sync)
        {
            CloseOutput();
            DeviceId = deviceId;
            OpenOutput();
            _changed = true;
        }
        RaiseStateChanged();
    }

    // Dins de _sync.
    private void OpenOutput()
    {
        if (DeviceId is null) return;
        try
        {
            var output = _openOutput(DeviceId);
            if (output.SampleRate != _rate)
            {
                // Freqüència nova: memòries de les veus noves i els inicis es tornen a llegir.
                _rate = output.SampleRate;
                _voices = CreateVoices(_rate);
                foreach (var b in _buttons.Values) StartLoad(b);
            }
            _outputError = null;
            output.Stopped += ex => { if (ex is not null) _outputError = ex; };
            output.Init(new Mixer(this, _voices, _rate, output.Channels));
            output.Play();
            _output = output;
            OutputReady = true;
            ErrorMessage = null;
        }
        catch (Exception ex) when (ex is AudioDeviceException or IOException or InvalidOperationException or COMException)
        {
            OutputReady = false;
            ErrorMessage = ex is AudioDeviceException ? ex.Message : "No s'ha pogut obrir la sortida de la botonera.";
            _nextOutputAttempt = DateTime.UtcNow + OutputRetry;
        }
    }

    // Dins de _sync. El fil d'àudio ja no corre: les veus s'acaben aquí.
    private void CloseOutput()
    {
        var output = _output;
        _output = null;
        OutputReady = false;
        if (output is not null)
        {
            try { output.Stop(); } catch (Exception) { /* el dispositiu pot haver desaparegut */ }
            try { output.Dispose(); } catch (Exception) { }
        }
        foreach (var v in _voices)
        {
            if (!v.InUse) continue;
            v.Ended = true;
            ReleaseVoice(v);
        }
    }

    /// <summary>
    /// Revisió periòdica (fora del fil d'àudio): allibera les veus que s'han acabat, detecta si la sortida ha fallat
    /// i la torna a obrir quan torna.
    /// </summary>
    public void Tick()
    {
        lock (_sync)
        {
            foreach (var v in _voices)
            {
                if (v.InUse && v.Ended)
                {
                    if (v.ReaderError is { } error && v.Button is { } b)
                    {
                        b.Status = CartButtonStatus.Error;
                        b.ErrorMessage = error is AudioFileException ? error.Message : $"No s'ha pogut llegir {System.IO.Path.GetFileName(b.Path)}.";
                    }
                    ReleaseVoice(v);
                    _changed = true;
                }
            }

            if (_outputError is { } outputError && _output is not null)
            {
                CloseOutput();
                ErrorMessage = $"Sortida de la botonera no disponible: {outputError.Message}";
                _nextOutputAttempt = DateTime.UtcNow + OutputRetry;
                _changed = true;
            }
            else if (_output is null && DeviceId is not null && DateTime.UtcNow >= _nextOutputAttempt)
            {
                OpenOutput();
                if (OutputReady) _changed = true;
            }
        }
        RaiseStateChanged();
    }

    // Dins de _sync. La veu ja no sona (Ended): s'atura el seu fil de lectura i queda lliure.
    private static void ReleaseVoice(Voice v)
    {
        v.Active = false;
        v.ReaderStop = true;
        v.Reader?.Join(TimeSpan.FromSeconds(2));
        v.Reader = null;
        v.ReaderError = null;
        v.Button = null;
        v.InUse = false;
    }

    // -----------------------------------------------------------------------
    // Lectura de l'inici de cada botó (en segon pla)
    // -----------------------------------------------------------------------

    // Dins de _sync.
    private void StartLoad(CartButton button)
    {
        int version = ++button.LoadVersion;
        int rate = _rate;
        button.Status = CartButtonStatus.Loading;
        string path = button.Path;
        _ = Task.Run(() => LoadHead(button, path, rate, version));
    }

    private void LoadHead(CartButton button, string path, int rate, int version)
    {
        float[]? head = null;
        bool whole = false;
        TimeSpan duration = default;
        CartButtonStatus status;
        string? error = null;
        try
        {
            using var source = AudioFileSource.Open(path, rate);
            duration = source.Duration;
            var buffer = new float[(int)(HeadSeconds * rate) * 2];
            int total = 0, read;
            while (total < buffer.Length && (read = source.Read(buffer.AsSpan(total))) > 0) total += read;
            // Si s'ha omplert, potser encara n'hi ha més; si no, és tot el fitxer.
            whole = total < buffer.Length;
            head = total == buffer.Length ? buffer : buffer[..total];
            status = CartButtonStatus.Ready;
        }
        catch (AudioFileException ex)
        {
            status = File.Exists(path) ? CartButtonStatus.Error : CartButtonStatus.Missing;
            error = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            status = CartButtonStatus.Error;
            error = $"No s'ha pogut llegir {System.IO.Path.GetFileName(path)}.";
        }

        lock (_sync)
        {
            if (button.LoadVersion != version) return; // mentrestant ha canviat (un altre fitxer o una altra freqüència)
            button.Head = head;
            button.HeadRate = rate;
            button.HeadIsWhole = whole;
            button.Duration = duration;
            button.Status = status;
            button.ErrorMessage = error;
            _changed = true;
        }
        RaiseStateChanged();
    }

    // -----------------------------------------------------------------------

    private static float DbToGain(double db) => (float)Math.Pow(10, db / 20);

    private static Voice[] CreateVoices(int rate) =>
        Enumerable.Range(0, MaxVoices).Select(_ => new Voice(rate)).ToArray();

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

    public void Dispose()
    {
        lock (_sync) CloseOutput();
    }

    /// <summary>
    /// Una veu: un botó que sona. Els camps els comparteixen el fil de les ordres (que la prepara i l'allibera) i el fil
    /// d'àudio (que la fa sonar mentre <see cref="Active"/> i no <see cref="Ended"/>). La memòria es crea una sola vegada.
    /// </summary>
    private sealed class Voice(int rate)
    {
        public readonly int Rate = rate;
        // Ple uns quants segons per davant (el fil de lectura té tot l'inici, 3 s, per començar).
        public readonly SampleRingBuffer Ring = new(rate * 2 * 4);

        // Fil de les ordres.
        public bool InUse;
        public CartButton? Button;
        public Thread? Reader;
        public volatile bool ReaderStop;
        public volatile Exception? ReaderError;

        // Compartits.
        public volatile bool Active;
        public volatile bool Ended;
        public volatile bool StopRequested;
        public volatile bool SourceFinished;
        public volatile float Gain = 1f;
        public long FramesPlayed;
        public int Underruns;

        // Només el fil d'àudio (els prepara el de les ordres abans d'Active).
        public float[] Head = [];
        public int HeadPos;
        public int StopFadeLeft;
    }

    /// <summary>
    /// El tros de la botonera que corre al <b>fil d'àudio</b>: barreja les veus actives. No crea objectes, no espera,
    /// no llegeix el disc i no escriu registres. El que passaria de ±1 es retalla.
    /// </summary>
    private sealed class Mixer : IWaveProvider
    {
        private readonly CartWall _owner;
        private readonly Voice[] _voices;
        private readonly int _channels;
        private readonly int _chunkFrames;
        private readonly int _stopFadeFrames;
        private readonly float[] _mix;
        private readonly float[] _voice;

        public Mixer(CartWall owner, Voice[] voices, int rate, int channels)
        {
            _owner = owner;
            _voices = voices;
            _channels = channels;
            _chunkFrames = rate / 50; // 20 ms
            _stopFadeFrames = Math.Max(1, rate * StopFadeMs / 1000);
            _mix = new float[_chunkFrames * 2];
            _voice = new float[_chunkFrames * 2];
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(Span<byte> buffer)
        {
            var output = MemoryMarshal.Cast<byte, float>(buffer);
            int frames = output.Length / _channels;
            for (int done = 0; done < frames;)
            {
                int n = Math.Min(frames - done, _chunkFrames);
                MixChunk(n);
                var target = output.Slice(done * _channels, n * _channels);
                if (_channels == 2)
                {
                    _mix.AsSpan(0, n * 2).CopyTo(target);
                }
                else
                {
                    target.Clear();
                    for (int i = 0; i < n; i++)
                    {
                        target[i * _channels] = _mix[i * 2];
                        if (_channels > 1) target[i * _channels + 1] = _mix[i * 2 + 1];
                    }
                }
                done += n;
            }
            return buffer.Length;
        }

        private void MixChunk(int n)
        {
            int samples = n * 2;
            Array.Clear(_mix, 0, samples);
            foreach (var v in _voices)
            {
                if (!v.Active || v.Ended) continue;

                // Primer l'inici (a la memòria des de sempre) i després el que ha llegit el fil de lectura.
                int got = 0;
                int fromHead = Math.Min(v.Head.Length - v.HeadPos, samples);
                if (fromHead > 0)
                {
                    Array.Copy(v.Head, v.HeadPos, _voice, 0, fromHead);
                    v.HeadPos += fromHead;
                    got = fromHead;
                }
                bool finished = false;
                if (got < samples)
                {
                    bool sourceFinished = v.SourceFinished; // abans de llegir: si ja ha acabat, el que falta és el final
                    got += v.Ring.Read(_voice.AsSpan(got, samples - got));
                    if (got < samples)
                    {
                        if (sourceFinished) finished = true;
                        else Interlocked.Increment(ref v.Underruns); // el disc no ha arribat a temps: silenci
                        Array.Clear(_voice, got, samples - got);
                    }
                }

                // Volum i, si s'ha d'aturar, un fos molt curt fins al silenci.
                float gain = v.Gain;
                bool stopping = v.StopRequested;
                for (int i = 0; i < n; i++)
                {
                    float g = gain;
                    if (stopping)
                    {
                        if (v.StopFadeLeft <= 0) { g = 0; finished = true; }
                        else g *= (float)v.StopFadeLeft-- / _stopFadeFrames;
                    }
                    _mix[i * 2] += _voice[i * 2] * g;
                    _mix[i * 2 + 1] += _voice[i * 2 + 1] * g;
                }

                Volatile.Write(ref v.FramesPlayed, Volatile.Read(ref v.FramesPlayed) + n);
                if (finished) v.Ended = true;
            }

            float left = 0, right = 0;
            for (int i = 0; i < samples; i += 2)
            {
                _mix[i] = Math.Clamp(_mix[i], -1f, 1f);
                _mix[i + 1] = Math.Clamp(_mix[i + 1], -1f, 1f);
                if (Math.Abs(_mix[i]) > left) left = Math.Abs(_mix[i]);
                if (Math.Abs(_mix[i + 1]) > right) right = Math.Abs(_mix[i + 1]);
            }
            _owner.Meter.Add(left, right);
        }
    }
}
