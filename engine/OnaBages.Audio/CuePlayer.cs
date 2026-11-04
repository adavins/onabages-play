namespace OnaBages.Audio;

/// <summary>Estats de la preescolta (especificació 7.2): no hi ha pausa, només sonar o no.</summary>
public enum CueState
{
    /// <summary>No sona (encara no ha sonat res, s'ha premut Stop o la pista s'ha acabat).</summary>
    Stopped,
    Playing,
    /// <summary>Sortida no disponible o fitxer il·legible.</summary>
    Error,
}

/// <summary>
/// Preescolta: escoltar una pista per auriculars, per una sortida pròpia, sense que surti a antena
/// (especificació 7). És independent de tots els reproductors: no en toca cap llista ni cap posició.
///
/// Per dins fa servir un <see cref="Player"/> amb una llista d'una sola pista: així aprofita la mateixa
/// lectura, la mateixa sortida i el mateix tractament d'errors, que ja estan provats.
/// Quan la pista s'acaba, s'atura (no passa a cap altra).
/// </summary>
public sealed class CuePlayer : IDisposable
{
    private readonly Player _player;
    private readonly Lock _sync = new();

    /// <param name="openOutput">Obre una sortida per identificador de dispositiu. Per defecte, WASAPI.</param>
    /// <param name="analyzer">Llegeix el format de la pista en segon pla (sense, el format no es coneix).</param>
    public CuePlayer(Func<string, IAudioOutput>? openOutput = null, TrackAnalyzer? analyzer = null)
    {
        _player = new Player("preescolta", openOutput, analyzer);
        _player.StateChanged += player =>
        {
            // La durada es guarda mentre la pista és oberta: quan s'atura, el reproductor la posa a zero.
            if (player.Duration > TimeSpan.Zero) _lastDuration = player.Duration;
            StateChanged?.Invoke(this);
        };
    }

    /// <summary>Es dispara quan canvia l'estat, la pista, la sortida, el volum o l'error.</summary>
    public event Action<CuePlayer>? StateChanged;

    public CueState State => _player.State switch
    {
        PlayerState.Playing => CueState.Playing,
        PlayerState.Error => CueState.Error,
        _ => CueState.Stopped,
    };

    public string? DeviceId => _player.DeviceId;
    public double VolumeDb => _player.VolumeDb;
    public string? ErrorMessage => _player.ErrorMessage;
    public string? ErrorDetail => _player.ErrorDetail;
    public int Underruns => _player.Underruns;

    /// <summary>L'última pista que s'ha preescoltat (es conserva quan s'atura, per tornar-hi).</summary>
    public string? FilePath { get; private set; }

    /// <summary>Durada de l'última pista (es conserva quan s'atura, per als botons de salt).</summary>
    public TimeSpan Duration
    {
        get
        {
            var current = _player.Duration;
            return current > TimeSpan.Zero ? current : _lastDuration;
        }
    }

    private TimeSpan _lastDuration;

    /// <summary>Posició dins la pista (0 si no sona).</summary>
    public TimeSpan Position => State == CueState.Playing ? _player.Position : TimeSpan.Zero;

    /// <summary>Format abreujat de la pista (també quan està aturada), si ja es coneix.</summary>
    public string? Format => _player.Format ?? _player.Playlist.Items.FirstOrDefault()?.Format;

    /// <summary>
    /// Fa sonar una pista des de <paramref name="startAt"/> (si ja en sonava una, la substitueix).
    /// Sense <paramref name="path"/>, torna a fer sonar l'última.
    /// </summary>
    public void Play(string? path = null, TimeSpan startAt = default)
    {
        lock (_sync)
        {
            path ??= FilePath ?? throw new PlayerCommandException("No hi ha cap pista per preescoltar.");
            if (!Path.IsPathFullyQualified(path))
                throw new PlayerCommandException("La ruta de la pista ha de ser completa.");
            if (DeviceId is null)
                throw new PlayerCommandException("Cal triar abans una sortida per a la preescolta.");

            // Una llista d'una sola pista: la d'abans se'n treu.
            _player.Playlist.Remove(_player.Playlist.Items.Select(i => i.Id).ToList());
            string itemId = _player.Playlist.Add([path])[0];

            if (!string.Equals(FilePath, path, StringComparison.OrdinalIgnoreCase))
                _lastDuration = TimeSpan.Zero;
            FilePath = path;

            _player.PlayItem(itemId, startAt);
        }
    }

    /// <summary>Atura la preescolta (no fa res si no sona).</summary>
    public void Stop()
    {
        lock (_sync)
        {
            if (_player.State is PlayerState.Playing or PlayerState.Paused or PlayerState.Ready)
                _player.Stop();
        }
    }

    /// <summary>Salta a un punt de la pista que sona. Si no sona, la fa sonar des d'allà.</summary>
    public void Seek(TimeSpan position)
    {
        lock (_sync)
        {
            if (State == CueState.Playing)
                _player.Seek(position);
            else
                Play(startAt: position);
        }
    }

    /// <summary>Volum de la preescolta, de −60 (silenci) a 0 dB.</summary>
    public void SetVolume(double db) => _player.SetVolume(db);

    /// <summary>Sortida de la preescolta. Si sonava, s'atura primer (no es pot canviar mentre sona).</summary>
    public void SetDevice(string deviceId)
    {
        lock (_sync)
        {
            if (_player.State == PlayerState.Playing) _player.Stop();
            _player.SetDevice(deviceId);
        }
    }

    /// <summary>Pic del que ha sortit des de l'última crida, per al mesurador.</summary>
    public (float Left, float Right) TakePeaks() => _player.TakePeaks();

    /// <summary>Revisió periòdica (detecta el final de la pista i els errors).</summary>
    public void Tick() => _player.Tick();

    public void Dispose() => _player.Dispose();
}
