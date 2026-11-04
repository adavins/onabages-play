using OnaBages.Audio;

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
/// </summary>
public sealed class EditorSession : IDisposable
{
    private readonly Lock _sync = new();
    private CancellationTokenSource? _job;
    private DateTime _lastProgressEvent;

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

    /// <summary>Qualsevol canvi (també el progrés, com a molt 4 vegades per segon).</summary>
    public event Action? Changed;

    /// <summary>Obre un fitxer (en segon pla). El que hi hagués obert es tanca sense desar.</summary>
    public void Open(string path)
    {
        lock (_sync)
        {
            CancelJob();
            Path = path;
            Edit = null;
            Error = null;
            SavedPath = null;
            StartJob("opening", token => Index(path, token), onDone: index =>
            {
                Edit = new Mp3Edit(index);
            });
        }
        RaiseChanged();
    }

    /// <summary>Tanca el fitxer (sense desar). Si s'estava obrint o desant, s'atura.</summary>
    public void Close()
    {
        lock (_sync)
        {
            CancelJob();
            Path = null;
            Edit = null;
            Busy = null;
            Error = null;
            SavedPath = null;
        }
        RaiseChanged();
    }

    /// <summary>Fa un canvi a l'edició (eliminar, conservar, desfer, refer). Retorna si ha canviat res.</summary>
    public bool Change(Func<Mp3Edit, bool> change)
    {
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
            StartJob("saving", token =>
            {
                edit.Save(saved, p => ReportProgress(p), token);
                return Index(saved, token);
            }, onDone: index =>
            {
                Path = saved;
                Edit = new Mp3Edit(index);
                SavedPath = saved;
            });
        }
        RaiseChanged();
    }

    public void Dispose()
    {
        lock (_sync)
            CancelJob();
    }

    // -----------------------------------------------------------------------

    private Mp3Edit RequireReady()
    {
        if (Busy == "opening") throw new EditorStateException("El fitxer encara s'està obrint.");
        if (Busy == "saving") throw new EditorStateException("S'està desant: espera que acabi.");
        return Edit ?? throw new EditorStateException("No hi ha cap fitxer obert a l'editor.");
    }

    private Mp3FrameIndex Index(string path, CancellationToken token) => Mp3FrameIndex.Build(path, token);

    /// <summary>Feina en segon pla. En acabar (dins del bloqueig): <paramref name="onDone"/> o l'error a <see cref="Error"/>.</summary>
    private void StartJob(string busy, Func<CancellationToken, Mp3FrameIndex> work, Action<Mp3FrameIndex> onDone)
    {
        var job = new CancellationTokenSource();
        _job = job;
        Busy = busy;
        Progress = 0;

        var thread = new Thread(() =>
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
        })
        {
            IsBackground = true,
            Name = busy == "opening" ? "Editor: obrir" : "Editor: desar",
            Priority = ThreadPriority.BelowNormal, // mai per davant de l'àudio ni dels enregistraments
        };
        thread.Start();
    }

    private void CancelJob()
    {
        _job?.Cancel();
        _job = null;
        Busy = null;
    }

    private void ReportProgress(double progress)
    {
        Progress = progress;
        var now = DateTime.UtcNow;
        if (now - _lastProgressEvent < TimeSpan.FromMilliseconds(250)) return;
        _lastProgressEvent = now;
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();
}
