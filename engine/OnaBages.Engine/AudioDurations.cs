using OnaBages.Audio;

namespace OnaBages.Engine;

/// <summary>
/// Durada dels àudios de l'explorador del panell lateral (fase 5, pas 15a; D-104).
///
/// Llegir la durada de molts fitxers (sobretot en una unitat de xarxa) pot trigar segons: no es fa mai mentre es
/// respon una ordre (podria endarrerir un Play). <see cref="Lookup"/> dona les que ja se saben i posa les altres en
/// una cua; un fil de prioritat baixa les mesura i les avisa a grups amb <see cref="Measured"/>.
/// Es recorden a la memòria mentre el fitxer no canviï de mida ni de data.
/// </summary>
public sealed class AudioDurations
{
    /// <summary>Com a molt, tantes durades per avís (i un avís cada tant de temps, encara que n'hi hagi menys).</summary>
    private const int BatchSize = 100;
    private static readonly TimeSpan BatchTime = TimeSpan.FromMilliseconds(300);

    private readonly Func<string, TimeSpan?> _measure;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, (long Size, DateTime Modified, double? Seconds)> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Path, long Size, DateTime Modified)> _queue = [];
    private bool _working;

    /// <param name="measure">Com es mesura un fitxer. Les proves en passen un d'inventat.</param>
    public AudioDurations(Func<string, TimeSpan?>? measure = null)
    {
        _measure = measure ?? TrackInfo.QuickDuration;
    }

    /// <summary>Durades mesurades (segons; null si no s'ha pogut llegir), per ruta. Arriba des del fil de mesura.</summary>
    public event Action<IReadOnlyDictionary<string, double?>>? Measured;

    /// <summary>
    /// Les durades que ja se saben dels fitxers d'una carpeta (la resta, null) i quantes queden per mesurar.
    /// Les que falten passen a la cua, en lloc de les d'una carpeta anterior (s'ha canviat de carpeta).
    /// </summary>
    public (Dictionary<string, double?> Known, int Pending) Lookup(IEnumerable<ExplorerEntry> files)
    {
        var known = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        lock (_sync)
        {
            _queue.Clear();
            foreach (var file in files)
            {
                if (_known.TryGetValue(file.Path, out var k) && k.Size == file.Size && k.Modified == file.Modified)
                    known[file.Path] = k.Seconds;
                else
                    _queue.Add((file.Path, file.Size, file.Modified));
            }
            if (_queue.Count > 0 && !_working)
            {
                _working = true;
                var thread = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Durades de l'explorador" };
                thread.Start();
            }
            return (known, _queue.Count);
        }
    }

    private void Work()
    {
        var batch = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        var since = DateTime.UtcNow;
        while (true)
        {
            (string Path, long Size, DateTime Modified) next;
            lock (_sync)
            {
                if (_queue.Count == 0)
                {
                    _working = false;
                    break;
                }
                next = _queue[0];
                _queue.RemoveAt(0);
            }

            double? seconds = _measure(next.Path) is { } d ? Math.Round(d.TotalSeconds, 1) : null;
            lock (_sync) _known[next.Path] = (next.Size, next.Modified, seconds);
            batch[next.Path] = seconds;

            if (batch.Count >= BatchSize || DateTime.UtcNow - since >= BatchTime)
            {
                Send(batch);
                batch = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
                since = DateTime.UtcNow;
            }
        }
        if (batch.Count > 0) Send(batch);
    }

    private void Send(Dictionary<string, double?> batch)
    {
        try { Measured?.Invoke(batch); }
        catch (Exception) { /* qui escolta no pot aturar les mesures */ }
    }
}
