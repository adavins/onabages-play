using System.Collections.Concurrent;

namespace OnaBages.Audio;

/// <summary>
/// Comprova les pistes que s'afegeixen a les llistes, d'una en una, en un fil de fons
/// (un per a tot el motor, perquè afegir cent pistes no posi el disc al límit).
/// Per a cada pista:
/// 1. <see cref="TrackInfo.Probe"/> (durada i format) i el resultat a la llista;
/// 2. després, amb menys prioritat (quan no queda cap pista per comprovar), la sonoritat
///    (<see cref="TrackLoudness.Measure"/>; fase 4, pas 5b), primer de la memòria cau si n'hi ha.
/// </summary>
public sealed class TrackAnalyzer : IDisposable
{
    private readonly BlockingCollection<(Playlist Playlist, string Id, string Path)> _probes = new();
    private readonly BlockingCollection<(Playlist Playlist, string Id, string Path)> _loudness = new();
    private readonly Thread? _thread;
    private readonly bool _synchronous;
    private readonly ILoudnessCache? _cache;
    private readonly CancellationTokenSource _stopping = new();

    /// <param name="synchronous">Per a les proves: comprova la pista a l'instant, sense fil de fons.</param>
    /// <param name="cache">Memòria cau de la sonoritat (null: sempre es mesura).</param>
    public TrackAnalyzer(bool synchronous = false, ILoudnessCache? cache = null)
    {
        _synchronous = synchronous;
        _cache = cache;
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
        {
            if (Probe(playlist, id, path))
                MeasureLoudness(playlist, id, path);
        }
        else if (!_probes.IsAddingCompleted)
        {
            _probes.Add((playlist, id, path));
        }
    }

    private void Run()
    {
        var queues = new[] { _probes, _loudness };
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                // TakeFromAny prova les cues en ordre: les comprovacions passen per davant de les mesures.
                int which = BlockingCollection<(Playlist, string, string)>.TakeFromAny(queues, out var job, _stopping.Token);
                var (playlist, id, path) = job;
                if (which == 0)
                {
                    if (Probe(playlist, id, path) && !_loudness.IsAddingCompleted)
                        _loudness.Add(job);
                }
                else
                {
                    MeasureLoudness(playlist, id, path);
                }
            }
        }
        catch (OperationCanceledException) { /* el motor s'atura */ }
        catch (ArgumentException) { /* les cues s'han tancat */ }
    }

    /// <summary>Durada i format. Retorna si la pista es pot llegir (llavors se'n mesura la sonoritat).</summary>
    private static bool Probe(Playlist playlist, string id, string path)
    {
        try
        {
            playlist.SetProbeResult(id, TrackInfo.Probe(path), null);
            return true;
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
        return false;
    }

    private void MeasureLoudness(Playlist playlist, string id, string path)
    {
        if (playlist.Find(id) is null) return; // s'ha tret de la llista mentrestant
        try
        {
            var loudness = _cache?.Read(path);
            if (loudness is null)
            {
                loudness = TrackLoudness.Measure(path, _stopping.Token);
                _cache?.Write(path, loudness);
            }
            playlist.SetAnalysis(id, loudness);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Sense mesura, la pista sona sense normalitzar (ja s'ha comprovat que es pot llegir).
        }
    }

    public void Dispose()
    {
        _probes.CompleteAdding();
        _loudness.CompleteAdding();
        _stopping.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
    }
}
