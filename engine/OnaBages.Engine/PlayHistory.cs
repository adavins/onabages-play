using OnaBages.Audio;

namespace OnaBages.Engine;

/// <summary>Una pista que ha sonat per antena, per a l'historial (especificació 6.6).</summary>
/// <param name="StartedAt">Quan va començar a sonar (hora local).</param>
/// <param name="Program">Nom del programa en aquell moment (buit si no n'hi havia).</param>
/// <param name="PlayerId">Reproductor que la va fer sonar.</param>
/// <param name="FilePath">Fitxer.</param>
/// <param name="Played">Temps que ha sonat de debò (sense les pauses).</param>
/// <param name="Duration">Durada total de la pista.</param>
public sealed record PlayedTrack(DateTime StartedAt, string Program, string PlayerId, string FilePath, TimeSpan Played, TimeSpan Duration);

/// <summary>
/// Segueix què sona a cada reproductor i avisa (<see cref="Finished"/>) quan una pista deixa de sonar:
/// ha acabat, s'ha premut Stop o n'ha començat una altra. La preescolta no hi entra: no surt per antena.
///
/// No llegeix ni escriu res: el motor el crida a cada revisió (10 vegades per segon) amb el que veu
/// de cada reproductor, i qui escolta <see cref="Finished"/> ho escriu al fitxer.
/// </summary>
public sealed class PlayHistory
{
    /// <summary>Les pistes que sonen menys d'això no es compten (p. ex. un Play i Stop de seguida per error).</summary>
    public static readonly TimeSpan MinPlayed = TimeSpan.FromSeconds(2);

    /// <summary>Si entre dues revisions passa més d'això, només es compta això (el motor s'ha aturat una estona).</summary>
    private static readonly TimeSpan MaxStep = TimeSpan.FromSeconds(1);

    private sealed class Current
    {
        public required string ItemId;
        public required string FilePath;
        public required DateTime StartedAt;
        public required string Program;
        public TimeSpan Duration;
        public TimeSpan Played;
        public DateTime LastSeen;
        public bool WasPlaying;
    }

    private readonly Dictionary<string, Current> _current = [];

    /// <summary>Una pista ha deixat de sonar (i ha sonat almenys <see cref="MinPlayed"/>).</summary>
    public event Action<PlayedTrack>? Finished;

    /// <summary>
    /// El que es veu ara d'un reproductor. Una pista comença a comptar quan sona; continua mentre
    /// sigui la pista actual (també en pausa o en error, per si es reprèn) i s'acaba quan el reproductor
    /// s'atura o passa a una altra pista. El temps només corre mentre sona.
    /// </summary>
    public void Observe(string playerId, PlayerState state, string? itemId, string? filePath, TimeSpan duration, DateTime now, string program)
    {
        if (_current.TryGetValue(playerId, out var current))
        {
            AddTime(current, now);
            if (itemId != current.ItemId || state is PlayerState.Empty or PlayerState.Ended)
            {
                _current.Remove(playerId);
                Report(playerId, current);
                current = null;
            }
            else
            {
                current.WasPlaying = state == PlayerState.Playing;
                if (duration > TimeSpan.Zero) current.Duration = duration;
            }
        }

        if (current is null && state == PlayerState.Playing && itemId is not null && filePath is not null)
        {
            _current[playerId] = new Current
            {
                ItemId = itemId,
                FilePath = filePath,
                StartedAt = now,
                Program = program,
                Duration = duration,
                LastSeen = now,
                WasPlaying = true,
            };
        }
    }

    /// <summary>En aturar el motor: el que sonava també queda a l'historial.</summary>
    public void FinishAll(DateTime now)
    {
        foreach (var (playerId, current) in _current.ToList())
        {
            AddTime(current, now);
            Report(playerId, current);
        }
        _current.Clear();
    }

    private static void AddTime(Current current, DateTime now)
    {
        if (current.WasPlaying && now > current.LastSeen)
        {
            var step = now - current.LastSeen;
            current.Played += step > MaxStep ? MaxStep : step;
        }
        current.LastSeen = now;
    }

    private void Report(string playerId, Current current)
    {
        if (current.Played < MinPlayed) return;
        Finished?.Invoke(new PlayedTrack(current.StartedAt, current.Program, playerId, current.FilePath, current.Played, current.Duration));
    }
}
