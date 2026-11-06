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
            // Després de canviar de perfil el motor s'atura: ja no es desa res, perquè no s'escrigui
            // la configuració d'aquest perfil a sobre de l'elecció del nou.
            if (_switchingTo is not null) return;
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

    // -----------------------------------------------------------------------
    // Perfils (fase 4, pas 1b)
    // -----------------------------------------------------------------------

    private string? _switchingTo;

    /// <summary>Perfil que serà l'actiu quan el motor torni a arrencar (null si no s'ha canviat).</summary>
    public string? SwitchingTo
    {
        get { lock (_sync) return _switchingTo; }
    }

    /// <summary>Carpeta de perfils (per obrir-la des de la interfície).</summary>
    public string ProfilesFolder => _repository.ProfilesFolder;

    /// <summary>Tots els perfils, amb l'actiu marcat.</summary>
    public List<(ProfileInfo Profile, bool Active)> ListProfiles()
    {
        lock (_sync)
        {
            EnsureActiveSaved();
            return _repository.ListProfiles()
                .Select(p => (p, string.Equals(p.Id, _config.ProfileId, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
    }

    /// <summary>Crea un perfil, buit o còpia d'un altre (<paramref name="copyFromId"/>).</summary>
    /// <exception cref="ProfileException">Nom no vàlid o repetit, perfil d'origen inexistent o error de disc.</exception>
    public ProfileInfo CreateProfile(string name, string? copyFromId)
    {
        lock (_sync)
        {
            EnsureActiveSaved();
            string clean = CheckName(name, exceptId: null);
            if (copyFromId is not null)
                RequireProfile(copyFromId);
            return _repository.CreateProfile(clean, copyFromId);
        }
    }

    /// <summary>Canvia el nom d'un perfil (la carpeta es queda igual).</summary>
    public ProfileInfo RenameProfile(string id, string name)
    {
        lock (_sync)
        {
            EnsureActiveSaved();
            string realId = RequireProfile(id);
            string clean = CheckName(name, exceptId: realId);
            if (IsActive(realId))
                Update(c => c.ProfileName = clean);
            else
                _repository.RenameProfile(realId, clean);
            return new ProfileInfo(realId, clean);
        }
    }

    /// <summary>Elimina un perfil. L'actiu no es pot eliminar.</summary>
    public void DeleteProfile(string id)
    {
        lock (_sync)
        {
            EnsureActiveSaved();
            string realId = RequireProfile(id);
            if (IsActive(realId))
                throw new ProfileException("bad_state", "No es pot eliminar el perfil actiu. Canvia primer a un altre perfil.");
            _repository.DeleteProfile(realId);
        }
    }

    /// <summary>
    /// Deixa triat el perfil per a la pròxima arrencada del motor. A partir d'ara no es desa res més
    /// d'aquest perfil (el motor s'aturarà de seguida). Qui crida ha de comprovar abans que no hi hagi res actiu.
    /// </summary>
    public ProfileInfo SwitchProfile(string id)
    {
        lock (_sync)
        {
            EnsureActiveSaved();
            string realId = RequireProfile(id);
            if (IsActive(realId))
                throw new ProfileException("bad_params", "Aquest ja és el perfil actiu.");
            var profile = _repository.ListProfiles().First(p => p.Id == realId);
            try
            {
                _repository.SetActiveProfile(_config, realId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ProfileException("file_error", $"No s'ha pogut canviar de perfil: {ex.Message}");
            }
            _switchingTo = realId;
            return profile;
        }
    }

    /// <summary>Desa un perfil en un .zip per passar-lo a un altre ordinador.</summary>
    public ProfileInfo ExportProfile(string id, string zipPath)
    {
        lock (_sync)
        {
            EnsureActiveSaved();
            string realId = RequireProfile(id);
            _repository.ExportProfile(realId, zipPath);
            return _repository.ListProfiles().First(p => p.Id == realId);
        }
    }

    /// <summary>
    /// Importa un perfil d'un .zip com a perfil nou (no canvia l'actiu). Sense <paramref name="name"/>,
    /// el del perfil del .zip; si ja n'hi ha un que es diu així, error <c>profile_exists</c> (la interfície
    /// pot demanar-ne un altre i tornar-ho a provar).
    /// </summary>
    public ProfileInfo ImportProfile(string zipPath, string? name)
    {
        lock (_sync)
        {
            EnsureActiveSaved();
            if (!File.Exists(zipPath))
                throw new ProfileException("file_error", $"No es troba el fitxer {zipPath}.");
            string wanted = name ?? _repository.ReadImportName(zipPath);
            string clean = CheckName(wanted, exceptId: null, repeatedCode: "profile_exists");
            return _repository.ImportProfile(zipPath, clean);
        }
    }

    // -----------------------------------------------------------------------
    // PIN de cada perfil (fase 5, pas 13a). Protecció contra canvis per error: la comprova la interfície.
    // -----------------------------------------------------------------------

    /// <summary>Si <paramref name="pin"/> és el PIN del perfil. Un perfil sense PIN accepta qualsevol PIN.</summary>
    public bool CheckPin(string id, string pin)
    {
        lock (_sync)
        {
            string realId = RequireProfile(id);
            string? stored = IsActive(realId) ? _config.ProfilePin : _repository.ReadProfilePin(realId);
            return stored is null || stored == pin;
        }
    }

    /// <summary>Posa el PIN d'un perfil (4 a 8 xifres) o el treu (null).</summary>
    /// <exception cref="ProfileException">bad_params si el PIN no és vàlid o el perfil no existeix.</exception>
    public void SetPin(string id, string? pin)
    {
        if (pin is not null && !EngineConfig.IsValidPin(pin))
            throw new ProfileException("bad_params",
                $"El PIN ha de tenir de {EngineConfig.MinPinLength} a {EngineConfig.MaxPinLength} xifres.");
        lock (_sync)
        {
            EnsureActiveSaved();
            string realId = RequireProfile(id);
            if (IsActive(realId))
                Update(c => c.ProfilePin = pin);
            else
                _repository.SetProfilePin(realId, pin);
        }
    }

    private bool IsActive(string id) => string.Equals(id, _config.ProfileId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// El perfil actiu ha d'existir a la carpeta per poder-lo llistar o copiar. Normalment ja hi és
    /// (es desa a cada canvi); en una instal·lació nova, encara no.
    /// </summary>
    private void EnsureActiveSaved()
    {
        if (_switchingTo is null && !File.Exists(_repository.ProfileFilePath(_config.ProfileId)))
        {
            try { _repository.Save(_config); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Warning?.Invoke($"No s'ha pogut desar la configuració: {ex.Message}");
            }
        }
    }

    /// <summary>L'identificador real del perfil (Windows no distingeix majúscules), o error si no existeix.</summary>
    private string RequireProfile(string id)
    {
        var found = _repository.ListProfiles().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        return found?.Id ?? throw new ProfileException("bad_params", $"No hi ha cap perfil «{id}».");
    }

    /// <summary>Nom net; no pot ser buit, massa llarg ni igual que el d'un altre perfil.</summary>
    private string CheckName(string name, string? exceptId, string repeatedCode = "bad_params")
    {
        string clean = ConfigRepository.CleanProgramName(name, cut: false);
        if (clean.Length == 0)
            throw new ProfileException("bad_params", "El perfil ha de tenir un nom.");
        if (clean.Length > EngineConfig.MaxProgramName)
            throw new ProfileException("bad_params", $"El nom del perfil pot tenir com a molt {EngineConfig.MaxProgramName} caràcters.");
        bool repeated = _repository.ListProfiles().Any(p =>
            !string.Equals(p.Id, exceptId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.Name, clean, StringComparison.CurrentCultureIgnoreCase));
        if (repeated)
            throw new ProfileException(repeatedCode, $"Ja hi ha un perfil que es diu «{clean}».");
        return clean;
    }
}
