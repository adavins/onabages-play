namespace OnaBages.Audio;

/// <summary>Estat d'una pista de la llista.</summary>
public enum TrackStatus
{
    /// <summary>S'està comprovant en segon pla.</summary>
    Analyzing,
    /// <summary>Comprovada: sonarà.</summary>
    Ok,
    /// <summary>Malmesa o format no compatible (<see cref="PlaylistItem.Problem"/> diu per què).</summary>
    Error,
    /// <summary>El fitxer ja no hi és (o la unitat no està disponible).</summary>
    Missing,
}

/// <summary>Una pista de la llista, tal com és en un moment donat (còpia: no canvia).</summary>
/// <param name="Id">Identificador propi dins de la llista (no canvia encara que es mogui).</param>
/// <param name="Name">Nom per mostrar: el nom del fitxer sense extensió (no es fan servir les metadades).</param>
/// <param name="Duration">Durada, quan ja s'ha comprovat.</param>
/// <param name="Format">Format abreujat («MP3 192 kbps estèreo 44,1 kHz»), quan ja s'ha comprovat.</param>
/// <param name="Loop">Es repeteix fins que s'atura.</param>
/// <param name="AutoNext">Quan s'acaba, sona la pista amb cue o la següent (un sol pas).</param>
public sealed record PlaylistItem(
    string Id,
    string Path,
    string Name,
    TimeSpan? Duration,
    string? Format,
    TrackStatus Status,
    string? Problem,
    bool Loop,
    bool AutoNext);

/// <summary>
/// Llista de reproducció d'un reproductor (especificació 6.3 i 6.4, D-031). Només dades i regles: no fa so.
///
/// Es fa servir des de diversos fils (ordres, anàlisi de pistes, fil de lectura del reproductor),
/// per això tot passa per un bloqueig propi. L'avís <see cref="Changed"/> es dispara fora del bloqueig.
/// </summary>
public sealed class Playlist
{
    private readonly Lock _sync = new();
    private readonly List<PlaylistItem> _items = [];
    // On era cada pista esborrada: si s'esborra la pista que sona, «la següent» és la que ocupava el seu lloc.
    private readonly Dictionary<string, int> _removedAt = [];
    private readonly TrackAnalyzer? _analyzer;
    private int _nextId;

    /// <param name="analyzer">Comprova les pistes en segon pla. Sense, queden «analitzant» (proves).</param>
    public Playlist(TrackAnalyzer? analyzer = null)
    {
        _analyzer = analyzer;
    }

    /// <summary>Ha canviat alguna cosa (pistes, ordre, marques, resultat d'una comprovació).</summary>
    public event Action? Changed;

    /// <summary>Pista marcada amb cue (la que sonarà a continuació), o null. Només n'hi pot haver una.</summary>
    public string? CueId { get; private set; }

    /// <summary>Còpia de les pistes, en ordre.</summary>
    public IReadOnlyList<PlaylistItem> Items
    {
        get { lock (_sync) return _items.ToList(); }
    }

    /// <summary>Suma de les durades conegudes.</summary>
    public TimeSpan TotalDuration
    {
        get { lock (_sync) return TimeSpan.FromTicks(_items.Sum(i => i.Duration?.Ticks ?? 0)); }
    }

    public PlaylistItem? Find(string? id)
    {
        if (id is null) return null;
        lock (_sync) return _items.Find(i => i.Id == id);
    }

    /// <summary>
    /// Afegeix fitxers abans de la pista <paramref name="beforeId"/> (o al final, si és null).
    /// Cada pista es comprova en segon pla. Retorna els identificadors nous, en ordre.
    /// </summary>
    public IReadOnlyList<string> Add(IEnumerable<string> paths, string? beforeId = null)
    {
        var added = new List<PlaylistItem>();
        lock (_sync)
        {
            int index = IndexOrEnd(beforeId);
            foreach (string path in paths)
            {
                var item = new PlaylistItem(
                    Id: $"t{++_nextId}",
                    Path: path,
                    Name: System.IO.Path.GetFileNameWithoutExtension(path),
                    Duration: null, Format: null,
                    Status: TrackStatus.Analyzing, Problem: null,
                    Loop: false, AutoNext: false);
                _items.Insert(index++, item);
                added.Add(item);
            }
        }

        foreach (var item in added)
            _analyzer?.Enqueue(this, item.Id, item.Path);
        RaiseChanged();
        return added.Select(i => i.Id).ToList();
    }

    /// <summary>
    /// Afegeix còpies de pistes (d'aquesta llista o d'una altra) davant de <paramref name="beforeId"/>
    /// (o al final). Es conserven les marques de bucle i d'avanç automàtic i el resultat de la
    /// comprovació (no cal tornar-les a comprovar); el cue no es copia. Retorna els identificadors nous.
    /// </summary>
    public IReadOnlyList<string> AddCopies(IEnumerable<PlaylistItem> sources, string? beforeId = null)
    {
        var added = new List<string>();
        lock (_sync)
        {
            int index = IndexOrEnd(beforeId);
            foreach (var source in sources)
            {
                var copy = source with { Id = $"t{++_nextId}" };
                _items.Insert(index++, copy);
                added.Add(copy.Id);
            }
        }
        RaiseChanged();
        return added;
    }

    /// <summary>Posa una còpia de cada pista just a sota de l'original. Retorna els identificadors nous.</summary>
    public IReadOnlyList<string> Duplicate(IReadOnlyCollection<string> ids)
    {
        var added = new List<string>();
        lock (_sync)
        {
            var set = ids.ToHashSet();
            for (int i = 0; i < _items.Count; i++)
            {
                if (!set.Contains(_items[i].Id)) continue;
                var copy = _items[i] with { Id = $"t{++_nextId}" };
                _items.Insert(++i, copy); // just a sota (i se salta la còpia)
                added.Add(copy.Id);
            }
        }
        RaiseChanged();
        return added;
    }

    /// <summary>
    /// Mou les pistes <paramref name="ids"/> (es manté el seu ordre) davant de <paramref name="beforeId"/>
    /// (o al final). Es pot fer també amb la pista que sona.
    /// </summary>
    public void Move(IReadOnlyCollection<string> ids, string? beforeId)
    {
        lock (_sync)
        {
            var set = ids.ToHashSet();
            var moving = _items.Where(i => set.Contains(i.Id)).ToList();
            if (moving.Count == 0) return;

            // Si el destí és una de les pistes que es mouen, es pren la primera de després que no es mogui.
            int target = beforeId is null ? _items.Count : _items.FindIndex(i => i.Id == beforeId);
            while (target >= 0 && target < _items.Count && set.Contains(_items[target].Id)) target++;
            string? anchor = target >= 0 && target < _items.Count ? _items[target].Id : null;

            _items.RemoveAll(i => set.Contains(i.Id));
            _items.InsertRange(IndexOrEnd(anchor), moving);
        }
        RaiseChanged();
    }

    /// <summary>Treu pistes de la llista. Si una sonava, el reproductor l'acaba igualment.</summary>
    public void Remove(IReadOnlyCollection<string> ids)
    {
        lock (_sync)
        {
            var set = ids.ToHashSet();
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (!set.Contains(_items[i].Id)) continue;
                _removedAt[_items[i].Id] = i;
                _items.RemoveAt(i);
            }
            if (CueId is not null && set.Contains(CueId)) CueId = null;
        }
        RaiseChanged();
    }

    /// <summary>Canvia les marques d'una pista. Els paràmetres null no es toquen.</summary>
    /// <param name="cue">true: aquesta és la següent (treu el cue de qualsevol altra). false: treu-lo.</param>
    /// <exception cref="KeyNotFoundException">Si la pista no és a la llista.</exception>
    public void SetFlags(string id, bool? loop = null, bool? autoNext = null, bool? cue = null)
    {
        lock (_sync)
        {
            int index = _items.FindIndex(i => i.Id == id);
            if (index < 0) throw new KeyNotFoundException(id);

            var item = _items[index];
            bool newLoop = loop ?? item.Loop;
            // Bucle i avanç automàtic no poden anar junts (D-034): amb el bucle no es pot activar
            // l'avanç; i activar el bucle apaga l'avanç que hi hagués.
            if (autoNext == true && newLoop)
                throw new PlayerCommandException("Aquesta pista té el bucle activat: no pot tenir també l'avanç automàtic.");
            bool newAutoNext = loop == true ? false : autoNext ?? item.AutoNext;
            _items[index] = item with { Loop = newLoop, AutoNext = newAutoNext };
            if (cue == true) CueId = id;
            else if (cue == false && CueId == id) CueId = null;
        }
        RaiseChanged();
    }

    /// <summary>La pista ha començat a sonar: si tenia el cue, s'apaga (D-031).</summary>
    public void ClearCueIf(string id)
    {
        lock (_sync)
        {
            if (CueId != id) return;
            CueId = null;
        }
        RaiseChanged();
    }

    /// <summary>
    /// Pista que va després de <paramref name="id"/> en l'ordre actual (especificació 6.4).
    /// Si <paramref name="id"/> és null, la primera. Si s'ha esborrat, la que ocupava el seu lloc.
    /// </summary>
    public string? NextAfter(string? id)
    {
        lock (_sync)
        {
            int index;
            if (id is null)
                index = 0;
            else
            {
                int at = _items.FindIndex(i => i.Id == id);
                index = at >= 0 ? at + 1 : _removedAt.GetValueOrDefault(id, _items.Count);
            }
            return index < _items.Count ? _items[index].Id : null;
        }
    }

    /// <summary>Resultat de la comprovació en segon pla (<see cref="TrackAnalyzer"/>).</summary>
    public void SetProbeResult(string id, TrackProbe? probe, string? problem)
    {
        Update(id, item => probe is not null
            ? item with { Duration = probe.Duration, Format = probe.Format, Status = TrackStatus.Ok, Problem = null }
            : item with { Status = TrackStatus.Error, Problem = problem });
    }

    /// <summary>El reproductor no ha pogut obrir la pista en anar-la a fer sonar.</summary>
    public void MarkError(string id, string problem) =>
        Update(id, item => item with { Status = TrackStatus.Error, Problem = problem });

    /// <summary>
    /// Comprova que els fitxers encara existeixen (cada 30 s, en segon pla). Només mira si hi són:
    /// no els obre. Si un fitxer que faltava torna, es torna a comprovar sencer.
    /// </summary>
    public void CheckFiles()
    {
        // Es copia la llista i es comprova fora del bloqueig: una unitat de xarxa lenta no ha d'aturar res.
        var snapshot = Items;
        var exists = snapshot.ToDictionary(i => i.Id, i => SafeExists(i.Path));

        var reprobe = new List<PlaylistItem>();
        bool changed = false;
        lock (_sync)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (!exists.TryGetValue(item.Id, out bool present)) continue; // afegida mentre es comprovava

                if (!present && item.Status != TrackStatus.Missing)
                {
                    _items[i] = item with { Status = TrackStatus.Missing, Problem = "El fitxer ja no hi és (o la unitat no està disponible)." };
                    changed = true;
                }
                else if (present && item.Status == TrackStatus.Missing)
                {
                    _items[i] = item with { Status = TrackStatus.Analyzing, Problem = null };
                    reprobe.Add(item);
                    changed = true;
                }
            }
        }

        foreach (var item in reprobe)
            _analyzer?.Enqueue(this, item.Id, item.Path);
        if (changed) RaiseChanged();
    }

    // -----------------------------------------------------------------------

    private void Update(string id, Func<PlaylistItem, PlaylistItem> change)
    {
        lock (_sync)
        {
            int index = _items.FindIndex(i => i.Id == id);
            if (index < 0) return; // s'ha esborrat mentre es comprovava
            _items[index] = change(_items[index]);
        }
        RaiseChanged();
    }

    private int IndexOrEnd(string? id)
    {
        int index = id is null ? -1 : _items.FindIndex(i => i.Id == id);
        return index < 0 ? _items.Count : index;
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    private void RaiseChanged() => Changed?.Invoke();
}
