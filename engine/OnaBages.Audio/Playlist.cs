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
/// <param name="IsSection">És un separador de secció (pas 9c): no és cap fitxer i no sona mai. <c>Name</c> és el títol.</param>
/// <param name="Color">Color del separador (un de <see cref="Playlist.SectionColors"/>), o null.</param>
/// <param name="Collapsed">Separador plegat: la interfície n'amaga les pistes fins al separador següent.</param>
/// <param name="LoudnessLufs">Sonoritat (LUFS), quan ja s'ha mesurat (fase 4, pas 5b). Null = encara no, o silenci.</param>
/// <param name="Sound">On hi ha so per a cada llindar (retall de silencis, fase 5), quan ja s'ha analitzat.</param>
/// <param name="EmptyChannel">El canal sense so, si la pista és estèreo però només sona per un (fase 5, pas 29a; D-132).</param>
/// <param name="BothChannels">Tria de l'operador (botó del reproductor): true, sonar pels dos canals; false, no tocar-ho;
/// null, automàtic (es corregeix si té un canal buit). Es desa amb la llista.</param>
public sealed record PlaylistItem(
    string Id,
    string Path,
    string Name,
    TimeSpan? Duration,
    string? Format,
    TrackStatus Status,
    string? Problem,
    bool Loop,
    bool AutoNext,
    bool IsSection = false,
    string? Color = null,
    bool Collapsed = false,
    double? LoudnessLufs = null,
    IReadOnlyList<SoundBounds>? Sound = null,
    EmptyChannel EmptyChannel = EmptyChannel.None,
    bool? BothChannels = null)
{
    /// <summary>
    /// Com ha de sonar (D-132): amb un canal buit, el bo pels dos (tret que l'operador ho hagi desactivat); sense, i
    /// amb el botó activat, els dos canals barrejats (així no es perd res de cap dels dos).
    /// </summary>
    public ChannelFix ChannelFix => BothChannels switch
    {
        false => ChannelFix.None,
        _ when EmptyChannel == EmptyChannel.Left => ChannelFix.UseRight,
        _ when EmptyChannel == EmptyChannel.Right => ChannelFix.UseLeft,
        true => ChannelFix.Mix,
        _ => ChannelFix.None,
    };
}

/// <summary>
/// Un element per afegir a una llista (p. ex. llegit d'una llista desada): un fitxer amb les seves marques
/// o, si <paramref name="SectionTitle"/> no és null, un separador de secció.
/// </summary>
public sealed record PlaylistEntry(string Path, bool Loop = false, bool AutoNext = false,
    string? SectionTitle = null, string? Color = null, bool Collapsed = false, bool? BothChannels = null)
{
    public bool IsSection => SectionTitle is not null;

    /// <summary>Un separador de secció.</summary>
    public static PlaylistEntry Section(string title, string? color = null, bool collapsed = false) =>
        new("", SectionTitle: title, Color: color, Collapsed: collapsed);

    /// <summary>
    /// Un fitxer. Si el nom és només guions baixos o guions (com «______.mp3», el separador que es feia
    /// servir al Winamp), es converteix en un separador de secció.
    /// </summary>
    public static PlaylistEntry ForFile(string path, bool loop = false, bool autoNext = false, bool? bothChannels = null) =>
        Playlist.IsSeparatorFileName(path) ? Section(Playlist.DefaultSectionTitle) : new(path, loop, autoNext, BothChannels: bothChannels);
}

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

    /// <summary>Colors possibles d'un separador de secció (la interfície decideix com es veuen).</summary>
    public static readonly string[] SectionColors = ["red", "orange", "yellow", "green", "teal", "blue", "purple", "pink"];

    /// <summary>Títol dels separadors que vénen d'un fitxer «______.mp3».</summary>
    public const string DefaultSectionTitle = "Secció";

    /// <summary>Llargada màxima del títol d'una secció.</summary>
    public const int MaxSectionTitle = 80;

    /// <summary>
    /// El nom del fitxer (sense extensió) és només guions baixos, guions, iguals o punts, com
    /// «______.mp3»: el truc del Winamp per separar seccions amb un fitxer de 0 segons.
    /// </summary>
    public static bool IsSeparatorFileName(string path)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(path).Trim();
        return name.Length >= 3 && name.All(c => c is '_' or '-' or '=' or '.' or ' ');
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
    public IReadOnlyList<string> Add(IEnumerable<string> paths, string? beforeId = null) =>
        AddEntries(paths.Select(p => PlaylistEntry.ForFile(p)), beforeId);

    /// <summary>Afegeix un separador de secció davant de <paramref name="beforeId"/> (o al final). Retorna el seu identificador.</summary>
    public string AddSection(string title, string? color = null, string? beforeId = null) =>
        AddEntries([PlaylistEntry.Section(title, color)], beforeId)[0];

    /// <summary>Canvia el títol, el color o si està plegat d'un separador. Els paràmetres null no es toquen.</summary>
    /// <param name="color">Un de <see cref="SectionColors"/>, o "" per treure'l.</param>
    /// <exception cref="KeyNotFoundException">Si no és a la llista.</exception>
    /// <exception cref="PlayerCommandException">Si no és un separador.</exception>
    public void UpdateSection(string id, string? title = null, string? color = null, bool? collapsed = null)
    {
        lock (_sync)
        {
            int index = _items.FindIndex(i => i.Id == id);
            if (index < 0) throw new KeyNotFoundException(id);
            var item = _items[index];
            if (!item.IsSection) throw new PlayerCommandException("Aquest element no és un separador de secció.");
            _items[index] = item with
            {
                Name = title is null ? item.Name : CleanTitle(title),
                Color = color is null ? item.Color : ValidColor(color),
                Collapsed = collapsed ?? item.Collapsed,
            };
        }
        RaiseChanged();
    }

    /// <summary>
    /// Com <see cref="Add"/>, però amb les marques de cada pista (p. ex. d'una llista desada).
    /// </summary>
    public IReadOnlyList<string> AddEntries(IEnumerable<PlaylistEntry> entries, string? beforeId = null)
    {
        var added = new List<PlaylistItem>();
        lock (_sync)
        {
            int index = IndexOrEnd(beforeId);
            foreach (var entry in entries)
            {
                var item = entry.IsSection
                    ? new PlaylistItem(
                        Id: $"t{++_nextId}", Path: "", Name: CleanTitle(entry.SectionTitle!),
                        Duration: null, Format: null, Status: TrackStatus.Ok, Problem: null,
                        Loop: false, AutoNext: false,
                        IsSection: true, Color: ValidColor(entry.Color), Collapsed: entry.Collapsed)
                    : new PlaylistItem(
                        Id: $"t{++_nextId}",
                        Path: entry.Path,
                        Name: System.IO.Path.GetFileNameWithoutExtension(entry.Path),
                        Duration: null, Format: null,
                        Status: TrackStatus.Analyzing, Problem: null,
                        Loop: entry.Loop, AutoNext: entry.AutoNext && !entry.Loop,
                        BothChannels: entry.BothChannels);
                _items.Insert(index++, item);
                added.Add(item);
            }
        }

        foreach (var item in added.Where(i => !i.IsSection))
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
    /// <param name="bothChannels">Sonar pels dos canals (true) o no tocar-ho (false); D-132.</param>
    /// <exception cref="KeyNotFoundException">Si la pista no és a la llista.</exception>
    public void SetFlags(string id, bool? loop = null, bool? autoNext = null, bool? cue = null, bool? bothChannels = null)
    {
        lock (_sync)
        {
            int index = _items.FindIndex(i => i.Id == id);
            if (index < 0) throw new KeyNotFoundException(id);

            var item = _items[index];
            if (item.IsSection && (loop == true || autoNext == true || cue == true || bothChannels is not null))
                throw new PlayerCommandException("Un separador de secció no sona: no pot tenir cue, bucle ni avanç automàtic.");
            bool newLoop = loop ?? item.Loop;
            // Bucle i avanç automàtic no poden anar junts (D-034): amb el bucle no es pot activar
            // l'avanç; i activar el bucle apaga l'avanç que hi hagués.
            if (autoNext == true && newLoop)
                throw new PlayerCommandException("Aquesta pista té el bucle activat: no pot tenir també l'avanç automàtic.");
            bool newAutoNext = loop == true ? false : autoNext ?? item.AutoNext;
            _items[index] = item with { Loop = newLoop, AutoNext = newAutoNext, BothChannels = bothChannels ?? item.BothChannels };
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
            // Els separadors de secció se salten: «la següent» és la primera pista de després.
            while (index < _items.Count && _items[index].IsSection) index++;
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

    /// <summary>
    /// Sonoritat mesurada en segon pla (fase 4, pas 5b; D-071). Null = silenci (no es normalitza).
    /// </summary>
    public void SetLoudness(string id, double? lufs) =>
        Update(id, item => item with { LoudnessLufs = lufs });

    /// <summary>Resultat de l'anàlisi en segon pla: sonoritat i on hi ha so (fase 5; D-079).</summary>
    public void SetAnalysis(string id, TrackLoudness analysis) =>
        Update(id, item => item with { LoudnessLufs = analysis.Lufs, Sound = analysis.Sound, EmptyChannel = analysis.EmptyChannel });

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
        var snapshot = Items.Where(i => !i.IsSection).ToList(); // els separadors no són fitxers
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

    /// <summary>Títol d'una secció: sense espais als extrems, com a molt <see cref="MaxSectionTitle"/> caràcters; buit, «Secció».</summary>
    private static string CleanTitle(string title)
    {
        string clean = title.Trim();
        if (clean.Length == 0) return DefaultSectionTitle;
        return clean.Length > MaxSectionTitle ? clean[..MaxSectionTitle] : clean;
    }

    /// <summary>El color, si és un dels permesos; si no (o és buit), cap.</summary>
    private static string? ValidColor(string? color) =>
        color is not null && SectionColors.Contains(color) ? color : null;

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
