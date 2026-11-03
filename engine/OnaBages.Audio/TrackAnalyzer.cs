using System.Collections.Concurrent;

namespace OnaBages.Audio;

/// <summary>
/// Comprova les pistes que s'afegeixen a les llistes, d'una en una, en un fil de fons
/// (un per a tot el motor, perquè afegir cent pistes no posi el disc al límit).
/// Per a cada pista: <see cref="TrackInfo.Probe"/> i el resultat a la llista.
/// </summary>
public sealed class TrackAnalyzer : IDisposable
{
    private readonly BlockingCollection<(Playlist Playlist, string Id, string Path)> _queue = new();
    private readonly Thread? _thread;
    private readonly bool _synchronous;

    /// <param name="synchronous">Per a les proves: comprova la pista a l'instant, sense fil de fons.</param>
    public TrackAnalyzer(bool synchronous = false)
    {
        _synchronous = synchronous;
        if (synchronous) return;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Anàlisi de pistes",
            Priority = ThreadPriority.BelowNormal, // mai per davant de la lectura dels reproductors
        };
        _thread.Start();
    }

    public void Enqueue(Playlist playlist, string id, string path)
    {
        if (_synchronous)
            Analyze(playlist, id, path);
        else if (!_queue.IsAddingCompleted)
            _queue.Add((playlist, id, path));
    }

    private void Run()
    {
        foreach (var (playlist, id, path) in _queue.GetConsumingEnumerable())
            Analyze(playlist, id, path);
    }

    private static void Analyze(Playlist playlist, string id, string path)
    {
        try
        {
            playlist.SetProbeResult(id, TrackInfo.Probe(path), null);
        }
        catch (AudioFileException ex)
        {
            playlist.SetProbeResult(id, null, ex.Message);
        }
        catch (Exception ex)
        {
            // Cap error d'una pista no pot aturar l'anàlisi de les altres.
            playlist.SetProbeResult(id, null, $"No s'ha pogut comprovar el fitxer: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread?.Join(TimeSpan.FromSeconds(2));
    }
}
