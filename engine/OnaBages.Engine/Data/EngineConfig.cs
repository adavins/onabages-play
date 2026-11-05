using OnaBages.Audio;

namespace OnaBages.Engine.Data;

/// <summary>Dispositiu desat a la configuració: identificador de Windows i nom (per si l'identificador canvia).</summary>
public sealed record DeviceRef(string Id, string Name);

/// <summary>Ajustos d'un enregistrador. Null = el valor per defecte.</summary>
public sealed record RecorderSettings(string? Name, string? Folder, double GainDb);

/// <summary>Preset propi del compressor/limitador (fase 4, D-069): desat al perfil amb un nom.</summary>
public sealed record ProcessingPreset(string Id, string Name, DynamicsSettings Settings);

/// <summary>Una carpeta de l'explorador (fase 4, pas 2a): el nom que es mostra i la ruta completa.</summary>
public sealed record ExplorerFolder(string Name, string Path);

/// <summary>
/// La configuració de treball: la de l'ordinador (<c>config.json</c>) i la del perfil actiu
/// (<c>perfils\&lt;id&gt;\perfil.json</c>), juntes (fase 4, D-057). Només la llegeix i l'escriu
/// <see cref="ConfigRepository"/>, que sap què va a cada fitxer; la resta del motor no ho ha de saber.
/// </summary>
public sealed class EngineConfig
{
    // --- Perfil actiu (fase 4) ---

    /// <summary>Identificador del perfil actiu: el nom de la seva carpeta dins de «perfils».</summary>
    public string ProfileId { get; set; } = ConfigRepository.DefaultProfileId;

    /// <summary>Nom del perfil, tal com es mostra (p. ex. «La Central»).</summary>
    public string ProfileName { get; set; } = ConfigRepository.DefaultProfileName;

    // --- Del perfil: dispositius, enregistradors, distribució i programa. Del perfil, també les últimes llistes. ---
    // --- De l'ordinador (config.json): KnownDevices, UiZoom i EditorDevice. ---

    /// <summary>Sortida que vol cada reproductor (clau: identificador del reproductor).</summary>
    public Dictionary<string, DeviceRef> Players { get; } = [];

    /// <summary>Entrada que vol cada enregistrador (clau: identificador de l'enregistrador).</summary>
    public Dictionary<string, DeviceRef> Recorders { get; } = [];

    /// <summary>Sortida de la preescolta (clau: <c>EngineState.CueId</c>; només n'hi ha una).</summary>
    public Dictionary<string, DeviceRef> Cue { get; } = [];

    /// <summary>Sortida de la reproducció de l'editor (fase 3). Null = la mateixa que la preescolta.</summary>
    public DeviceRef? EditorDevice { get; set; }

    /// <summary>Volum de la preescolta, en dB (de −60 a 0). Es recorda perquè depèn dels auriculars.</summary>
    public double CueVolumeDb { get; set; }

    /// <summary>Nom, carpeta de treball i volum de cada enregistrador (clau: identificador).</summary>
    public Dictionary<string, RecorderSettings> RecorderSettings { get; } = [];

    /// <summary>Dispositius que hi havia l'última vegada: serveix per detectar els nous i els formats canviats.</summary>
    public List<AudioDevice> KnownDevices { get; set; } = [];

    /// <summary>Escala de la interfície (1 = 100 %). Entre <see cref="MinZoom"/> i <see cref="MaxZoom"/>.</summary>
    public double UiZoom { get; set; } = 1.0;

    public const double MinZoom = 0.5;
    public const double MaxZoom = 1.5;

    // --- Distribució (pas 9 de la fase 2). El nombre d'elements s'aplica en arrencar el motor. ---

    /// <summary>Quants reproductors hi ha (de 1 a <see cref="MaxPlayers"/>).</summary>
    public int PlayerCount { get; set; } = 2;

    /// <summary>Quants enregistradors hi ha (de 1 a <see cref="MaxRecorders"/>).</summary>
    public int RecorderCount { get; set; } = 2;

    /// <summary>Enregistradors per fila a la interfície (de 1 a <see cref="MaxRecorders"/>).</summary>
    public int RecordersPerRow { get; set; } = 2;

    /// <summary>Segons abans del final en què el temps restant d'un reproductor canvia d'aspecte (0 = cap avís).</summary>
    public int EndWarningSeconds { get; set; } = 10;

    /// <summary>Nom del programa que es fa (p. ex. «Magazín dels dissabtes»). Surt a la barra superior i a l'historial. Buit = cap.</summary>
    public string ProgramName { get; set; } = "";

    public const int MaxProgramName = 60;

    // --- Compressor/limitador de cada reproductor (fase 4, pas 4b; del perfil) ---

    /// <summary>Ajustos del compressor/limitador de cada reproductor (clau: identificador). Sense, desactivat.</summary>
    public Dictionary<string, DynamicsSettings> PlayerDynamics { get; } = [];

    /// <summary>Presets propis del compressor/limitador, en l'ordre en què es van desar (D-069).</summary>
    public List<ProcessingPreset> ProcessingPresets { get; set; } = [];

    // --- Normalització de sonoritat (fase 4, pas 5b; del perfil) ---

    /// <summary>Reproductors que normalitzen (per defecte, cap: D-070).</summary>
    public HashSet<string> NormalizedPlayers { get; } = [];

    /// <summary>Objectiu de la normalització en LUFS (per defecte −12: D-070).</summary>
    public double LoudnessTargetLufs { get; set; } = DefaultLoudnessTargetLufs;

    public const double DefaultLoudnessTargetLufs = -12;
    public const double MinLoudnessTargetLufs = -24;
    public const double MaxLoudnessTargetLufs = -6;

    public const int MaxProcessingPresets = 30;
    public const int MaxProcessingPresetName = 40;

    // --- Explorador del panell lateral (fase 4, pas 2a; del perfil) ---

    /// <summary>Carpetes de la pestanya Àudios (p. ex. «Sintonies», «Talls»), en l'ordre que es mostren.</summary>
    public List<ExplorerFolder> AudioFolders { get; set; } = [];

    /// <summary>Carpetes de la pestanya Llistes (llistes desades .obpl, .m3u).</summary>
    public List<ExplorerFolder> ListFolders { get; set; } = [];

    public const int MaxExplorerFolders = 20;
    public const int MaxExplorerFolderName = 40;

    public const int MaxPlayers = 6;
    public const int MaxRecorders = 4;
    public const int MaxEndWarningSeconds = 60;
}
