using OnaBages.Audio;

namespace OnaBages.Engine;

/// <summary>Ordre que no es pot fer amb aquest enregistrament. El missatge és per a l'operador.</summary>
public sealed class RecordingCommandException(string message) : Exception(message);

/// <summary>Un enregistrament acabat (especificació 8.3).</summary>
/// <param name="Id">Identificador dins de la llista ("e1", "e2"...). Canvia en reiniciar el motor.</param>
/// <param name="Path">On és ara el fitxer (després de desar-lo, la ruta nova).</param>
/// <param name="Saved">L'operador l'ha desat (mogut o reanomenat).</param>
/// <param name="Busy">S'està movent (desant) en segon pla.</param>
/// <param name="ClosedCleanly">El fitxer es va tancar bé. Si no, cal recuperar-lo (pas 7).</param>
/// <param name="Problem">L'últim error en desar-lo, si n'hi ha hagut.</param>
public sealed record RecordingItem(
    string Id, string Path, string RecorderId, DateTimeOffset StartedAt, TimeSpan Duration, long FileSize,
    bool Saved, bool Busy, bool ClosedCleanly, string? Problem);

/// <summary>
/// Enregistraments acabats: el pendent de cada enregistrador i els recents (D-036).
///
/// Quan un enregistrament s'atura, s'hi afegeix. Mentre un enregistrador el té com a pendent,
/// la interfície el mostra al panell de l'enregistrador; quan se'n comença un altre, passa a
/// «Recents». Les accions (Desar, Descartar) són les mateixes en tots dos llocs.
/// </summary>
public sealed class RecordingList
{
    /// <summary>Com a molt, tants enregistraments a la llista (els més antics en surten; els fitxers no s'esborren).</summary>
    public const int MaxItems = 30;

    private readonly Lock _sync = new();
    private readonly List<RecordingItem> _items = []; // el més recent primer
    private readonly Func<string, bool> _isPending;
    private readonly Action<string> _sendToRecycleBin;
    private readonly bool _synchronous;
    private int _nextId;

    /// <param name="isPending">Si un fitxer és el pendent d'algun enregistrador (no surt de la llista ni es pot treure).</param>
    /// <param name="sendToRecycleBin">Envia un fitxer a la paperera. Les proves en passen una d'inventada.</param>
    /// <param name="synchronous">Desar sense fil a part (per a les proves).</param>
    public RecordingList(Func<string, bool>? isPending = null, Action<string>? sendToRecycleBin = null, bool synchronous = false)
    {
        _isPending = isPending ?? (_ => false);
        _sendToRecycleBin = sendToRecycleBin ?? RecycleBin.Send;
        _synchronous = synchronous;
    }

    /// <summary>Hi ha hagut algun canvi (pot venir d'un altre fil: un desat que acaba).</summary>
    public event Action? Changed;

    public IReadOnlyList<RecordingItem> Items
    {
        get { lock (_sync) return [.. _items]; }
    }

    public RecordingItem? Find(string id)
    {
        lock (_sync) return _items.Find(i => i.Id == id);
    }

    /// <summary>Un enregistrament que s'acaba d'aturar.</summary>
    public RecordingItem Add(FinishedRecording finished)
    {
        RecordingItem item;
        lock (_sync)
        {
            item = new RecordingItem(NewId(), finished.Path, finished.RecorderId, finished.StartedAt, finished.Duration,
                finished.FileSize, Saved: false, Busy: false, finished.ClosedCleanly, Problem: null);
            _items.Insert(0, item);
            Trim();
        }
        Changed?.Invoke();
        return item;
    }

    /// <summary>Els recents desats l'última vegada (en arrencar el motor). Arriben del més recent al més antic.</summary>
    public void AddLoaded(IEnumerable<RecordingItem> loaded)
    {
        lock (_sync)
        {
            foreach (var item in loaded)
                _items.Add(item with { Id = NewId(), Busy = false, Problem = null });
            Trim();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Desa l'enregistrament a <paramref name="targetPath"/> (mou o reanomena el fitxer).
    /// Mai no sobreescriu res: si ja existeix, s'hi afegeix _2, _3... Si cal copiar-lo a un altre
    /// disc, es fa en segon pla: mentrestant, l'element té <c>Busy</c>.
    /// </summary>
    /// <returns>La ruta on quedarà.</returns>
    public string Save(string id, string targetPath)
    {
        if (!System.IO.Path.IsPathFullyQualified(targetPath))
            throw new RecordingCommandException("La ruta per desar ha de ser completa.");
        if (!targetPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            targetPath += ".mp3";

        string source, target;
        lock (_sync)
        {
            var item = Existing(id);
            if (!File.Exists(item.Path))
                throw new RecordingCommandException($"El fitxer ja no hi és: {item.Path}");

            source = item.Path;
            if (string.Equals(System.IO.Path.GetFullPath(source), System.IO.Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
            {
                // Desar-lo on ja és: no cal moure res.
                Replace(item with { Saved = true, Problem = null });
                target = source;
            }
            else
            {
                target = UniquePath(targetPath);
                Replace(item with { Busy = true, Problem = null });
            }
        }
        Changed?.Invoke();

        if (target != source)
        {
            if (_synchronous) Move(id, source, target);
            else _ = Task.Run(() => Move(id, source, target));
        }
        return target;
    }

    /// <summary>Envia el fitxer a la paperera de Windows i el treu de la llista.</summary>
    public void Discard(string id)
    {
        string path;
        lock (_sync)
        {
            var item = Existing(id);
            path = item.Path;
        }

        if (File.Exists(path))
        {
            try { _sendToRecycleBin(path); }
            catch (Exception ex) { throw new RecordingCommandException($"No s'ha pogut enviar a la paperera: {ex.Message}"); }
        }

        lock (_sync) _items.RemoveAll(i => i.Id == id);
        Changed?.Invoke();
    }

    /// <summary>Treu l'enregistrament de la llista. El fitxer no es toca.</summary>
    public void Remove(string id)
    {
        lock (_sync)
        {
            var item = Existing(id);
            if (_isPending(item.Path))
                throw new RecordingCommandException("És l'enregistrament pendent d'un enregistrador: desa'l o descarta'l.");
            _items.Remove(item);
        }
        Changed?.Invoke();
    }

    // -----------------------------------------------------------------------

    private void Move(string id, string source, string target)
    {
        string? problem = null;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            // Al mateix disc és instantani; a un altre, copia i després esborra l'original.
            File.Move(source, target, overwrite: false);
        }
        catch (Exception ex)
        {
            problem = $"No s'ha pogut desar a {target}: {ex.Message}";
        }

        lock (_sync)
        {
            if (_items.Find(i => i.Id == id) is { } item)
                Replace(problem is null
                    ? item with { Path = target, Saved = true, Busy = false }
                    : item with { Busy = false, Problem = problem });
        }
        Changed?.Invoke();
    }

    /// <summary>L'element (dins de _sync). Si no hi és o s'està desant, error per a l'operador.</summary>
    private RecordingItem Existing(string id)
    {
        var item = _items.Find(i => i.Id == id)
            ?? throw new RecordingCommandException($"No hi ha cap enregistrament '{id}' a la llista.");
        if (item.Busy)
            throw new RecordingCommandException("Aquest enregistrament s'està desant. Espera que acabi.");
        return item;
    }

    private void Replace(RecordingItem item)
    {
        int index = _items.FindIndex(i => i.Id == item.Id);
        if (index >= 0) _items[index] = item;
    }

    /// <summary>Treu els més antics que sobrin, menys els pendents i els que s'estan desant.</summary>
    private void Trim()
    {
        for (int i = _items.Count - 1; i >= 0 && _items.Count > MaxItems; i--)
        {
            if (!_items[i].Busy && !_isPending(_items[i].Path))
                _items.RemoveAt(i);
        }
    }

    private string NewId() => $"e{++_nextId}";

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string folder = System.IO.Path.GetDirectoryName(path)!;
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        string extension = System.IO.Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = System.IO.Path.Combine(folder, $"{name}_{i}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
