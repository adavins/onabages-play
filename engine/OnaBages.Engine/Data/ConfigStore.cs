namespace OnaBages.Engine.Data;

/// <summary>
/// La configuració en memòria, compartida per tot el motor (assignacions, ajustos...).
/// Qualsevol canvi passa per <see cref="Update"/>, que el desa de seguida amb el repositori.
/// Així només hi ha una còpia de la configuració i una sola manera de desar-la.
/// </summary>
public sealed class ConfigStore
{
    private readonly ConfigRepository _repository;
    private readonly Lock _sync = new();
    private EngineConfig _config = new();

    public ConfigStore(ConfigRepository repository)
    {
        _repository = repository;
    }

    public string FilePath => _repository.FilePath;

    /// <summary>Avisos (p. ex. no s'ha pogut llegir o desar el fitxer).</summary>
    public event Action<string>? Warning;

    /// <summary>Llegeix el fitxer. Si no es pot fer servir, es continua amb una configuració buida.</summary>
    public ConfigLoadResult Load()
    {
        var loaded = _repository.Load();
        lock (_sync)
            _config = loaded.Config;
        if (loaded.Problem is not null)
            Warning?.Invoke(loaded.Problem);
        return loaded;
    }

    /// <summary>Llegeix un valor de la configuració.</summary>
    public T Read<T>(Func<EngineConfig, T> read)
    {
        lock (_sync)
            return read(_config);
    }

    /// <summary>Fa un canvi a la configuració i la desa.</summary>
    public void Update(Action<EngineConfig> change)
    {
        lock (_sync)
        {
            change(_config);
            try
            {
                _repository.Save(_config);
            }
            catch (Exception ex)
            {
                // Sense desar, el motor continua funcionant; només es perdria el canvi en reiniciar.
                Warning?.Invoke($"No s'ha pogut desar la configuració ({_repository.FilePath}): {ex.Message}");
            }
        }
    }
}
