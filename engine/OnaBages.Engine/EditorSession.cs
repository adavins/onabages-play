using OnaBages.Audio;
using OnaBages.Engine.Data;

namespace OnaBages.Engine;

/// <summary>Ordre de l'editor que no es pot fer ara (res obert, s'està obrint o desant...). Codi <c>bad_state</c>.</summary>
public sealed class EditorStateException(string message) : Exception(message);

/// <summary>Es vol desar a sobre d'un fitxer que ja existeix sense haver-ho confirmat. Codi <c>file_exists</c>.</summary>
public sealed class EditorFileExistsException(string message) : Exception(message);

/// <summary>
/// L'editor del motor (fase 3; especificació 9): un sol fitxer obert alhora.
/// Obrir (indexar) i desar poden trigar uns segons amb un fitxer d'una hora: es fan en un
/// fil propi de prioritat baixa, perquè mai no frenin els reproductors ni els enregistradors,
/// i mentrestant <see cref="Busy"/> diu què s'està fent. Les edicions són instantànies.
///
/// La forma d'ona (pas 2) es calcula després d'obrir, en un altre fil de prioritat baixa, i es
/// pot editar mentrestant. Es desa a la memòria cau (<see cref="PeakCacheFile"/>).
/// </summary>
public sealed class EditorSession : IDisposable
{
    private readonly Lock _sync = new();
    private readonly string? _cacheFolder;
    private CancellationTokenSource? _job;
    private CancellationTokenSource? _peaksJob;
    private DateTime _lastProgressEvent;
    private DateTime _lastPeaksEvent;
    private byte[]? _peaks;      // pics del fitxer obert (de l'original, no de la versió editada)
    private int _peaksDone;      // fotogrames que ja tenen pic
    private List<RecordingMark> _marks = []; // marques del fitxer obert (pas 23c), en temps de l'original

    /// <param name="cacheFolder">Carpeta de la memòria cau de formes d'ona. Per defecte, la de l'usuari; les proves en passen una de temporal.</param>
    /// <param name="openOutput">Sortida de la reproducció de l'editor. Per defecte, WASAPI; les proves en passen una de falsa.</param>
    public EditorSession(string? cacheFolder = null, Func<string, IAudioOutput>? openOutput = null)
    {
        _cacheFolder = cacheFolder;
        Player = new EditorPlayer(openOutput);
        Player.StateChanged += _ => RaiseChanged();
    }

    /// <summary>Reproducció de l'editor (pas 3): la versió editada, per una sortida pròpia.</summary>
    public EditorPlayer Player { get; }

    /// <summary>
    /// Fa sonar la versió editada des de <paramref name="from"/> fins a <paramref name="to"/> (segons;
    /// sense <paramref name="to"/>, fins al final) per <paramref name="deviceId"/>.
    /// </summary>
    public void Play(string deviceId, double from, double? to)
    {
        string path;
        List<PlayPiece> pieces;
        double start;
        Func<double, float>? gainAt;
        lock (_sync)
        {
            var edit = RequireReady();
            int startFrame = edit.FrameAt(from);
            int endFrame = to is { } t ? edit.FrameAt(t) : edit.FrameCount;
            if (startFrame >= edit.FrameCount)
                startFrame = 0; // al final: torna a començar (com els reproductors en prémer Play)
            if (endFrame <= startFrame)
                endFrame = edit.FrameCount;
            double frame = edit.Index.FrameSeconds;
            pieces = edit.SegmentsBetween(startFrame, endFrame)
                .Select(s => new PlayPiece(s.Start * frame, s.Count * frame))
                .ToList();
            path = edit.Index.Path;
            start = edit.SecondsAt(startFrame);
            // Fosos (fase 4, pas 6b): se senten a la previsualització. Els d'aquest moment (qualsevol canvi atura la reproducció).
            var gains = edit.Gains;
            gainAt = gains.IsEmpty ? null : edit.GainAt;
        }
        Player.Play(deviceId, path, pieces, start, gainAt); // fora del bloqueig: obre la sortida i espera el so preparat
    }

    /// <summary>Atura la reproducció de l'editor.</summary>
    public void StopPlayback() => Player.Stop();

    /// <summary>Fitxer obert (o que s'està obrint). Null si no n'hi ha cap.</summary>
    public string? Path { get; private set; }

    /// <summary>L'edició, quan el fitxer ja és obert.</summary>
    public Mp3Edit? Edit { get; private set; }

    /// <summary>"opening", "saving" o null.</summary>
    public string? Busy { get; private set; }

    /// <summary>Fracció feta de la feina en curs (0..1).</summary>
    public double Progress { get; private set; }

    /// <summary>Motiu de l'última feina que ha fallat (p. ex. no s'ha pogut desar). Es neteja amb la següent ordre.</summary>
    public string? Error { get; private set; }

    /// <summary>Fitxer on s'ha desat l'última vegada (fins al canvi següent).</summary>
    public string? SavedPath { get; private set; }

    /// <summary>Fotogrames de l'original que ja tenen forma d'ona (es pot anar dibuixant).</summary>
    public int PeaksDone => Volatile.Read(ref _peaksDone);

    /// <summary>La forma d'ona no s'ha pogut calcular (el fitxer es pot editar igualment).</summary>
    public string? PeaksError { get; private set; }

    /// <summary>
    /// Marques del fitxer obert (fase 5, pas 23c; de <c>Nom.mp3.marques</c>), on queden a la versió editada:
    /// les dels trossos eliminats no hi surten. Cada una amb el número que tenia a l'original.
    /// </summary>
    public List<(int Number, RecordingMark Mark)> EditedMarks()
    {
        lock (_sync)
        {
            if (Edit is not { } edit || Busy is not null) return [];
            return RecordingMarks.MapToEdited(_marks, edit.Segments, edit.Index.FrameSeconds, edit.Index.FrameCount);
        }
    }

    /// <summary>Qualsevol canvi (també el progrés, com a molt 4 vegades per segon).</summary>
    public event Action? Changed;

    /// <summary>Obre un fitxer (en segon pla). El que hi hagués obert es tanca sense desar.</summary>
    public void Open(string path)
    {
        Player.Stop(); // el que sonava és d'un altre fitxer
        lock (_sync)
        {
            CancelJob();
            CancelPeaks();
            Path = path;
            Edit = null;
            Error = null;
            SavedPath = null;
            _marks = [];
            StartJob("opening", token => Mp3FrameIndex.Build(path, token), onDone: index =>
            {
                _marks = RecordingMarks.Read(path); // fitxer petit: si no n'hi ha o és malmès, cap marca
                StartPeaks(index);
                // L'última: qui miri Edit sense bloqueig (les proves) ja ho troba tot a punt.
                Edit = new Mp3Edit(index);
            });
        }
        RaiseChanged();
    }

    /// <summary>Tanca el fitxer (sense desar). Si s'estava obrint o desant, s'atura.</summary>
    public void Close()
    {
        Player.Stop();
        lock (_sync)
        {
            CancelJob();
            CancelPeaks();
            Path = null;
            Edit = null;
            Busy = null;
            Error = null;
            SavedPath = null;
            _marks = [];
        }
        RaiseChanged();
    }

    /// <summary>Fa un canvi a l'edició (eliminar, conservar, desfer, refer). Retorna si ha canviat res.</summary>
    public bool Change(Func<Mp3Edit, bool> change)
    {
        Player.Stop(); // després del canvi, les posicions ja no serien les mateixes
        bool changed;
        lock (_sync)
        {
            var edit = RequireReady();
            Error = null;
            changed = change(edit);
            if (changed) SavedPath = null;
        }
        if (changed) RaiseChanged();
        return changed;
    }

    /// <summary>
    /// Desa la versió editada (en segon pla). Sense <paramref name="target"/>, a sobre de l'original.
    /// Si la destinació existeix, cal <paramref name="overwrite"/>. Quan acaba, l'editor continua
    /// amb el fitxer desat (com si s'hagués obert de nou: sense desfer).
    /// </summary>
    public void Save(string? target, bool overwrite)
    {
        Player.Stop();
        lock (_sync)
        {
            var edit = RequireReady();
            target = System.IO.Path.GetFullPath(target ?? edit.Index.Path);
            if (!target.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                throw new EditorStateException("El fitxer s'ha de desar amb l'extensió .mp3.");
            if (File.Exists(target) && !overwrite)
                throw new EditorFileExistsException($"Ja hi ha un fitxer «{System.IO.Path.GetFileName(target)}». Vols substituir-lo?");

            Error = null;
            string saved = target;
            var segments = edit.Segments;
            var gains = edit.Gains;
            // Les marques, on queden a la versió que es desa (pas 23c).
            var marks = RecordingMarks.MapToEdited(_marks, segments, edit.Index.FrameSeconds, edit.Index.FrameCount).Select(m => m.Mark).ToList();
            StartJob("saving", token =>
            {
                edit.Save(saved, p => ReportProgress(p), token);
                return Mp3FrameIndex.Build(saved, token);
            }, onDone: index =>
            {
                // L'àudio ja és desat. Les marques, al costat; si no n'hi ha cap, no hi ha d'haver fitxer
                // (si es desa a sobre i s'han eliminat totes). Si no es poden escriure, es diu.
                try
                {
                    if (marks.Count > 0) RecordingMarks.Write(saved, marks);
                    else if (File.Exists(RecordingMarks.PathFor(saved))) File.Delete(RecordingMarks.PathFor(saved));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Error = $"L'àudio s'ha desat, però les marques no: {ex.Message}";
                }
                _marks = marks;
                // Forma d'ona del fitxer desat: si la de l'original ja era completa, es munta amb els
                // trossos que s'han quedat (sense tornar a descodificar). Si no, es calcula de nou.
                byte[]? derived = _peaks is { } peaks && _peaksDone == edit.Index.FrameCount
                    ? WaveformPeaks.Splice(peaks, segments, gains)
                    : null;
                CancelPeaks();
                Path = saved;
                Edit = new Mp3Edit(index);
                SavedPath = saved;
                if (derived is not null && derived.Length == index.FrameCount * WaveformPeaks.BytesPerFrame)
                {
                    _peaks = derived;
                    Volatile.Write(ref _peaksDone, index.FrameCount);
                    PeakCacheFile.Write(index, derived, _cacheFolder);
                }
                else
                {
                    StartPeaks(index);
                }
            });
        }
        RaiseChanged();
    }

    /// <summary>
    /// Pics de la forma d'ona, en fotogrames de l'<b>original</b>: a partir de <paramref name="from"/>,
    /// com a molt <paramref name="count"/> (només els que ja estan calculats).
    /// </summary>
    public (int Start, byte[] Data) ReadPeaks(int from, int count)
    {
        lock (_sync)
        {
            if (Edit is null || _peaks is null)
                throw new EditorStateException("No hi ha cap fitxer obert a l'editor.");
            int done = _peaksDone;
            from = Math.Clamp(from, 0, done);
            count = Math.Clamp(count, 0, done - from);
            return (from, _peaks.AsSpan(from * WaveformPeaks.BytesPerFrame, count * WaveformPeaks.BytesPerFrame).ToArray());
        }
    }

    public void Dispose()
    {
        Player.Dispose();
        lock (_sync)
        {
            CancelJob();
            CancelPeaks();
        }
    }

    // -----------------------------------------------------------------------

    private Mp3Edit RequireReady()
    {
        if (Busy == "opening") throw new EditorStateException("El fitxer encara s'està obrint.");
        if (Busy == "saving") throw new EditorStateException("S'està desant: espera que acabi.");
        return Edit ?? throw new EditorStateException("No hi ha cap fitxer obert a l'editor.");
    }

    /// <summary>Feina en segon pla. En acabar (dins del bloqueig): <paramref name="onDone"/> o l'error a <see cref="Error"/>.</summary>
    private void StartJob(string busy, Func<CancellationToken, Mp3FrameIndex> work, Action<Mp3FrameIndex> onDone)
    {
        var job = new CancellationTokenSource();
        _job = job;
        Busy = busy;
        Progress = 0;

        StartThread(busy == "opening" ? "Editor: obrir" : "Editor: desar", () =>
        {
            Mp3FrameIndex? result = null;
            string? error = null;
            try { result = work(job.Token); }
            catch (OperationCanceledException) { return; }
            catch (Mp3EditException ex) { error = ex.Message; }
            catch (Exception ex) { error = $"Error inesperat de l'editor: {ex.Message}"; }

            lock (_sync)
            {
                if (_job != job) return; // l'han cancel·lat o n'ha començat una altra
                _job = null;
                Busy = null;
                Progress = 1;
                if (result is not null) onDone(result);
                else
                {
                    Error = error;
                    if (busy == "opening") { Path = null; Edit = null; }
                }
            }
            RaiseChanged();
        });
    }

    /// <summary>Forma d'ona: de la memòria cau si hi és; si no, es calcula en segon pla (dins de _sync).</summary>
    private void StartPeaks(Mp3FrameIndex index)
    {
        PeaksError = null;
        if (PeakCacheFile.Read(index, _cacheFolder) is { } cached)
        {
            _peaks = cached;
            Volatile.Write(ref _peaksDone, index.FrameCount);
            return;
        }

        var peaks = new byte[index.FrameCount * WaveformPeaks.BytesPerFrame];
        _peaks = peaks;
        Volatile.Write(ref _peaksDone, 0);
        var job = new CancellationTokenSource();
        _peaksJob = job;

        StartThread("Editor: forma d'ona", () =>
        {
            string? error = null;
            try
            {
                WaveformPeaks.Compute(index.Path, index.SampleRate, index.SamplesPerFrame, index.FrameCount, peaks,
                    done => ReportPeaks(job, done), job.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { error = ex.Message; }

            lock (_sync)
            {
                if (_peaksJob != job) return;
                _peaksJob = null;
                if (error is null)
                {
                    Volatile.Write(ref _peaksDone, index.FrameCount);
                    PeakCacheFile.Write(index, peaks, _cacheFolder);
                }
                else
                {
                    PeaksError = $"No s'ha pogut fer la forma d'ona: {error}";
                }
            }
            RaiseChanged();
        });
    }

    private static void StartThread(string name, Action work) => new Thread(() => work())
    {
        IsBackground = true,
        Name = name,
        Priority = ThreadPriority.BelowNormal, // mai per davant de l'àudio ni dels enregistraments
    }.Start();

    private void CancelJob()
    {
        _job?.Cancel();
        _job = null;
        Busy = null;
    }

    private void CancelPeaks()
    {
        _peaksJob?.Cancel();
        _peaksJob = null;
        _peaks = null;
        Volatile.Write(ref _peaksDone, 0);
        PeaksError = null;
    }

    private void ReportProgress(double progress)
    {
        Progress = progress;
        var now = DateTime.UtcNow;
        if (now - _lastProgressEvent < TimeSpan.FromMilliseconds(250)) return;
        _lastProgressEvent = now;
        RaiseChanged();
    }

    private void ReportPeaks(CancellationTokenSource job, int done)
    {
        lock (_sync)
        {
            if (_peaksJob != job) return; // és d'un fitxer que ja no és obert
            Volatile.Write(ref _peaksDone, done);
        }
        var now = DateTime.UtcNow;
        if (now - _lastPeaksEvent < TimeSpan.FromMilliseconds(250)) return;
        _lastPeaksEvent = now;
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();
}
