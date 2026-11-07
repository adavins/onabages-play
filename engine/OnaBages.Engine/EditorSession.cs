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
    private Thread? _peaksThread; // el fil que calcula la forma d'ona (mentre ho fa, té el fitxer obert)
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

    /// <summary>
    /// El fitxer obert s'està enregistrant (fase 5, pas 24a; D-117): l'editor en veu una fotografia (fins on era en obrir-lo
    /// o en actualitzar-lo), que només es llegeix. Es pot escoltar, seleccionar i desar trossos en fitxers nous; no es pot
    /// retallar, fer fosos ni desar a sobre.
    /// </summary>
    public bool Live { get; private set; }

    private const string LiveNotAllowed =
        "Mentre s'enregistra, l'editor només deixa escoltar, seleccionar i desar trossos amb «Desar la selecció». " +
        "Per retallar o fer fosos, espera que s'aturi l'enregistrament.";

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

    /// <summary>On s'ha desat l'última selecció (pas 23d; fins a l'ordre següent). L'edició no canvia.</summary>
    public string? SavedSelectionPath { get; private set; }

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
            // Per ordre de posició: una marca moguda pot passar per davant d'una altra (i conserva el número).
            return [.. RecordingMarks.MapToEdited(_marks, edit.Segments, edit.Index.FrameSeconds, edit.Index.FrameCount)
                .OrderBy(m => m.Mark.Seconds)];
        }
    }

    /// <summary>
    /// Mou la marca <paramref name="number"/> a <paramref name="seconds"/> de la versió editada (fase 5, pas 23f; D-115).
    /// Les marques es guarden en temps de l'original: es fa la correspondència amb els trossos que es queden. <b>Es desa
    /// de seguida</b> al fitxer de marques del fitxer obert (no cal «Desar»: l'àudio no canvia i no es perd si es tanca
    /// sense desar). No es desfà amb <c>editor.undo</c>. Només les marques que es veuen (no les dels trossos eliminats).
    /// </summary>
    public void MoveMark(int number, double seconds)
    {
        lock (_sync)
        {
            var edit = RequireReady();
            if (Live) throw new EditorStateException(LiveMarks);
            double original = OriginalSecondsOf(edit, number, seconds);
            var marks = new List<RecordingMark>(_marks);
            marks[number - 1] = marks[number - 1] with { Seconds = original };
            WriteMarks(edit, marks);
        }
        RaiseChanged();
    }

    /// <summary>
    /// Afegeix una marca a <paramref name="seconds"/> de la versió editada (fase 5, pas 27a; D-125) i la desa de seguida al
    /// fitxer de marques (no es desfà amb <c>editor.undo</c>). Les marques queden ordenades per temps de l'original: les de
    /// després es renumeren. Retorna el número de la nova.
    /// </summary>
    public int AddMark(double seconds, string? name)
    {
        int number;
        lock (_sync)
        {
            var edit = RequireReady();
            if (Live) throw new EditorStateException(LiveMarks);
            var mark = new RecordingMark(OriginalSecondsAt(edit, seconds), name);
            var marks = new List<RecordingMark>(_marks);
            number = marks.FindLastIndex(m => m.Seconds <= mark.Seconds) + 2;
            marks.Insert(number - 1, mark);
            WriteMarks(edit, marks, sort: false);
        }
        RaiseChanged();
        return number;
    }

    /// <summary>Esborra la marca <paramref name="number"/> (pas 27a; D-125). Les de després es renumeren. Només les que es veuen.</summary>
    public void RemoveMark(int number)
    {
        lock (_sync)
        {
            var edit = RequireReady();
            if (Live) throw new EditorStateException(LiveMarks);
            RequireVisibleMark(edit, number);
            var marks = new List<RecordingMark>(_marks);
            marks.RemoveAt(number - 1);
            WriteMarks(edit, marks, sort: false);
        }
        RaiseChanged();
    }

    /// <summary>Posa (o treu, amb un nom buit) el nom de la marca <paramref name="number"/> (pas 27a; D-125).</summary>
    public void NameMark(int number, string? name)
    {
        lock (_sync)
        {
            var edit = RequireReady();
            if (Live) throw new EditorStateException(LiveMarks);
            RequireVisibleMark(edit, number);
            var marks = new List<RecordingMark>(_marks);
            marks[number - 1] = marks[number - 1] with { Name = name };
            WriteMarks(edit, marks, sort: false);
        }
        RaiseChanged();
    }

    /// <summary>
    /// Temps de l'original d'un punt de la versió editada (pas 27a: per afegir una marca a través de l'enregistrador, que és
    /// l'amo de les marques mentre enregistra o té el fitxer pendent).
    /// </summary>
    public double OriginalSeconds(double seconds)
    {
        lock (_sync) return OriginalSecondsAt(RequireReady(), seconds);
    }

    /// <summary>Comprova que la marca <paramref name="number"/> existeixi i es vegi (no sigui en un tros eliminat).</summary>
    public void CheckMark(int number)
    {
        lock (_sync) RequireVisibleMark(RequireReady(), number);
    }

    // Dins de _sync. Desa les marques al fitxer i, si ha anat bé, les fa servir (si no, es queden les d'abans i es diu).
    private void WriteMarks(Mp3Edit edit, List<RecordingMark> marks, bool sort = true)
    {
        if (sort) marks.Sort((a, b) => a.Seconds.CompareTo(b.Seconds));
        try
        {
            RecordingMarks.Write(edit.Index.Path, marks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new EditorStateException($"No s'han pogut desar les marques: {ex.Message}");
        }
        _marks = marks;
    }

    private const string LiveMarks = "Mentre s'enregistra, les marques es canvien a través de l'enregistrador.";

    /// <summary>
    /// On hauria d'anar la marca <paramref name="number"/> en temps de l'original si es mou a <paramref name="seconds"/> de
    /// la versió editada (pas 24b: per demanar-ho a l'enregistrador, que és l'amo de les marques mentre enregistra o té el
    /// fitxer pendent). Comprova que la marca existeixi i es vegi.
    /// </summary>
    public double OriginalSecondsOfMark(int number, double seconds)
    {
        lock (_sync) return OriginalSecondsOf(RequireReady(), number, seconds);
    }

    /// <summary>Torna a llegir les marques del fitxer (pas 24b: després que l'enregistrador n'hagi mogut una).</summary>
    public void ReloadMarks()
    {
        lock (_sync)
        {
            if (Edit is null || Path is null) return;
            _marks = RecordingMarks.Read(Path);
        }
        RaiseChanged();
    }

    /// <summary>Temps de la versió editada → temps de l'original, per a la marca <paramref name="number"/> (dins de _sync).</summary>
    private double OriginalSecondsOf(Mp3Edit edit, int number, double seconds)
    {
        RequireVisibleMark(edit, number);
        return OriginalSecondsAt(edit, seconds);
    }

    // Dins de _sync.
    private void RequireVisibleMark(Mp3Edit edit, int number)
    {
        if (number < 1 || number > _marks.Count)
            throw new EditorStateException($"No hi ha cap marca {number}.");
        if (!RecordingMarks.MapToEdited(_marks, edit.Segments, edit.Index.FrameSeconds, edit.Index.FrameCount).Any(m => m.Number == number))
            throw new EditorStateException($"La marca {number} és en un tros eliminat.");
    }

    /// <summary>Temps de la versió editada → temps de l'original (dins de _sync), amb els decimals del fotograma.</summary>
    private static double OriginalSecondsAt(Mp3Edit edit, double seconds)
    {
        double fs = edit.Index.FrameSeconds;
        double frame = Math.Clamp(seconds / fs, 0, edit.FrameCount);
        int whole = Math.Min((int)Math.Floor(frame), edit.FrameCount - 1);
        return (edit.ToOriginal(whole) + (frame - whole)) * fs;
    }

    /// <summary>Qualsevol canvi (també el progrés, com a molt 4 vegades per segon).</summary>
    public event Action? Changed;

    /// <summary>Obre un fitxer (en segon pla). El que hi hagués obert es tanca sense desar.</summary>
    /// <param name="live">El fitxer s'està enregistrant (pas 24a): se n'obre una fotografia que només es llegeix.</param>
    public void Open(string path, bool live = false)
    {
        Player.Stop(); // el que sonava és d'un altre fitxer
        lock (_sync)
        {
            if (Busy == "saving" && string.Equals(Path, path, StringComparison.OrdinalIgnoreCase))
                throw new EditorStateException("S'està desant: espera que acabi.");
            CancelJob();
            CancelPeaks();
            Path = path;
            Live = live;
            Edit = null;
            Error = null;
            SavedPath = null;
            SavedSelectionPath = null;
            _marks = [];
            StartJob("opening", token => Mp3FrameIndex.Build(path, token), onDone: index =>
            {
                _marks = RecordingMarks.Read(path); // fitxer petit: si no n'hi ha o és malmès, cap marca
                StartPeaks(index);
                // L'última: qui miri Edit sense bloqueig (les proves) ja ho troba tot a punt.
                Edit = new Mp3Edit(index) { Growing = live };
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
            Live = false;
            Edit = null;
            Busy = null;
            Error = null;
            SavedPath = null;
            SavedSelectionPath = null;
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
            if (Live) throw new EditorStateException(LiveNotAllowed);
            Error = null;
            SavedSelectionPath = null;
            changed = change(edit);
            if (changed) SavedPath = null;
        }
        if (changed) RaiseChanged();
        return changed;
    }

    /// <summary>Resultat de la normalització: pic d'ara, guany (múltiple de 1,5 dB) i pic que quedarà (dBFS).</summary>
    public sealed record NormalizePlan(double PeakDb, double GainDb, double ResultPeakDb, bool Limited);

    /// <summary>Guany màxim de la normalització: més enllà, només es pujaria el soroll de fons.</summary>
    public const double MaxNormalizeGainDb = 24;

    /// <summary>
    /// Normalització per pic (fase 5, pas 26a; D-123): puja o baixa [start, end) de la versió editada perquè el pic arribi
    /// a <paramref name="targetDb"/> sense passar-lo. El guany és un múltiple de 1,5 dB (sense recodificar, com els fosos),
    /// com a molt +24 dB. El pic surt de la forma d'ona (amb els fosos que hi hagi): no cal descodificar res.
    /// Amb <paramref name="apply"/> false només es calcula (per al diàleg).
    /// </summary>
    /// <exception cref="EditorStateException">Sense fitxer, ocupat, en directe (si s'aplica), forma d'ona a mitges o tot silenci.</exception>
    public NormalizePlan Normalize(int start, int end, double targetDb, bool apply)
    {
        NormalizePlan plan;
        lock (_sync)
        {
            var edit = RequireReady();
            if (apply && Live) throw new EditorStateException(LiveNotAllowed);
            if (_peaks is not { } peaks || _peaksDone < edit.Index.FrameCount)
                throw new EditorStateException("Encara s'està calculant la forma d'ona: torna-ho a provar d'aquí a uns segons.");

            // Pic de la versió editada: el de cada fotograma de l'original, amb el seu guany (el grànul més fort).
            // El pic desat està arrodonit a 1/255: es compta mig pas més, perquè mai no es passi de l'objectiu.
            double peak = 0;
            var gains = edit.Gains;
            foreach (var s in edit.SegmentsBetween(start, end))
            {
                for (int f = s.Start; f < s.End; f++)
                {
                    int raw = Math.Max(peaks[f * WaveformPeaks.BytesPerFrame], peaks[f * WaveformPeaks.BytesPerFrame + 1]);
                    if (raw == 0) continue;
                    double frame = (raw + 0.5) / 255;
                    if (gains.TryGetValue(f, out var g)) frame *= Mp3Edit.GainOfSteps(Math.Max(g.Granule0, g.Granule1));
                    if (frame > peak) peak = frame;
                }
            }
            if (peak <= 0)
                throw new EditorStateException(start == end ? "No hi ha res a normalitzar." : "És tot silenci: no hi ha res a normalitzar.");

            double peakDb = 20 * Math.Log10(peak);
            int maxSteps = (int)(MaxNormalizeGainDb / Mp3Gain.StepDb);
            int wanted = (int)Math.Floor((targetDb - peakDb) / Mp3Gain.StepDb + 1e-9);
            int steps = Math.Min(wanted, maxSteps);
            plan = new NormalizePlan(Math.Round(peakDb, 1), steps * Mp3Gain.StepDb, Math.Round(peakDb + steps * Mp3Gain.StepDb, 1), wanted > maxSteps);
            if (!apply || steps == 0) return plan;
        }

        Change(edit => edit.Gain(start, end, (int)Math.Round(plan.GainDb / Mp3Gain.StepDb)));
        return plan;
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
            if (Live) throw new EditorStateException(LiveNotAllowed);
            target = System.IO.Path.GetFullPath(target ?? edit.Index.Path);
            if (!target.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                throw new EditorStateException("El fitxer s'ha de desar amb l'extensió .mp3.");
            if (File.Exists(target) && !overwrite)
                throw new EditorFileExistsException($"Ja hi ha un fitxer «{System.IO.Path.GetFileName(target)}». Vols substituir-lo?");

            Error = null;
            SavedSelectionPath = null;
            string saved = target;
            var segments = edit.Segments;
            var gains = edit.Gains;
            // Les marques, on queden a la versió que es desa (pas 23c).
            var marks = RecordingMarks.MapToEdited(_marks, segments, edit.Index.FrameSeconds, edit.Index.FrameCount).Select(m => m.Mark).ToList();
            // Desar a sobre del fitxer obert mentre encara es calcula la forma d'ona (D-116): aquell fil té el fitxer obert
            // i Windows no deixaria substituir-lo. S'atura el càlcul (després de desar es fa de nou, del fitxer desat) i,
            // abans de substituir, s'espera que el fil l'hagi deixat.
            Thread? peaksReading = null;
            if (_peaksJob is not null && string.Equals(saved, System.IO.Path.GetFullPath(edit.Index.Path), StringComparison.OrdinalIgnoreCase))
            {
                peaksReading = _peaksThread;
                CancelPeaks();
            }
            StartJob("saving", token =>
            {
                peaksReading?.Join(TimeSpan.FromSeconds(10)); // s'atura en pocs mil·lisegons
                edit.Save(saved, p => ReportProgress(p), token);
                return Mp3FrameIndex.Build(saved, token);
            },
            // Si no s'ha pogut desar, el fitxer obert continua igual: la forma d'ona aturada es torna a calcular.
            onFailed: () =>
            {
                if (_peaks is null && Edit is { } current) StartPeaks(current.Index);
            },
            onDone: index =>
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
    /// Desa <b>només la selecció</b> [<paramref name="start"/>, <paramref name="end"/>) de la versió editada (segons) en un
    /// fitxer nou (fase 5, pas 23d; D-113), en segon pla. <b>L'edició no canvia</b>: es continua amb el mateix fitxer, els
    /// mateixos canvis i el mateix desfer. Les marques que hi ha dins de la selecció es desen al costat, comptades des del
    /// principi del fitxer nou. No es pot desar a sobre del fitxer obert. Si la destinació existeix, cal <paramref name="overwrite"/>.
    /// </summary>
    public void SaveSelection(double start, double end, string target, bool overwrite)
    {
        Player.Stop();
        lock (_sync)
        {
            var edit = RequireReady();
            int from = edit.FrameAt(start), to = edit.FrameAt(end);
            if (to <= from)
                throw new EditorStateException("Cal seleccionar un tros per desar-lo.");
            target = System.IO.Path.GetFullPath(target);
            if (!target.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                throw new EditorStateException("El fitxer s'ha de desar amb l'extensió .mp3.");
            if (string.Equals(target, System.IO.Path.GetFullPath(edit.Index.Path), StringComparison.OrdinalIgnoreCase))
                throw new EditorStateException("La selecció no es pot desar a sobre del fitxer obert: tria un altre nom.");
            if (File.Exists(target) && !overwrite)
                throw new EditorFileExistsException($"Ja hi ha un fitxer «{System.IO.Path.GetFileName(target)}». Vols substituir-lo?");

            Error = null;
            SavedSelectionPath = null;
            string saved = target;
            var only = edit.SegmentsBetween(from, to);
            // Marques de dins de la selecció, comptades des del seu principi (els trossos de la selecció fan de versió editada).
            var marks = RecordingMarks.MapToEdited(_marks, only, edit.Index.FrameSeconds, edit.Index.FrameCount).Select(m => m.Mark).ToList();
            StartJob("saving", token =>
            {
                edit.Save(saved, p => ReportProgress(p), token, only);
                return Mp3FrameIndex.Build(saved, token); // comprova que el fitxer nou es llegeix bé
            }, onDone: _ =>
            {
                SavedSelectionPath = saved;
                try
                {
                    if (marks.Count > 0) RecordingMarks.Write(saved, marks);
                    else if (File.Exists(RecordingMarks.PathFor(saved))) File.Delete(RecordingMarks.PathFor(saved));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Error = $"La selecció s'ha desat, però les marques no: {ex.Message}";
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

    /// <summary>
    /// Feina en segon pla. En acabar (dins del bloqueig): <paramref name="onDone"/> o l'error a <see cref="Error"/>
    /// (i, si n'hi ha, <paramref name="onFailed"/>).
    /// </summary>
    private void StartJob(string busy, Func<CancellationToken, Mp3FrameIndex> work, Action<Mp3FrameIndex> onDone, Action? onFailed = null)
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
                Progress = 1;
                if (result is not null) onDone(result);
                else
                {
                    Error = error;
                    if (busy == "opening") { Path = null; Edit = null; }
                    onFailed?.Invoke();
                }
                // L'últim: l'estat es llegeix sense bloqueig (EditorJson), i qui veu «no ocupat» ha de trobar el resultat
                // ja posat. Abans es deixava d'estar ocupat primer i, de tant en tant, es veia un fitxer obert sense Edit.
                Busy = null;
            }
            RaiseChanged();
        });
    }

    /// <summary>Forma d'ona: de la memòria cau si hi és; si no, es calcula en segon pla (dins de _sync).</summary>
    private void StartPeaks(Mp3FrameIndex index)
    {
        PeaksError = null;
        bool useCache = !Live; // d'un enregistrament en curs (pas 24a), cap: el fitxer encara creix
        if (useCache && PeakCacheFile.Read(index, _cacheFolder) is { } cached)
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

        _peaksThread = StartThread("Editor: forma d'ona", () =>
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
                    if (useCache) PeakCacheFile.Write(index, peaks, _cacheFolder);
                }
                else
                {
                    PeaksError = $"No s'ha pogut fer la forma d'ona: {error}";
                }
            }
            RaiseChanged();
        });
    }

    private static Thread StartThread(string name, Action work)
    {
        var thread = new Thread(() => work())
        {
            IsBackground = true,
            Name = name,
            Priority = ThreadPriority.BelowNormal, // mai per davant de l'àudio ni dels enregistraments
        };
        thread.Start();
        return thread;
    }

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
