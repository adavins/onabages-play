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

    /// <summary>
    /// PIN del perfil (fase 5, pas 13a), o null si no en té. És una protecció contra canvis per error, no seguretat:
    /// es desa en text pla a perfil.json i només el comprova la interfície (abans de triar el perfil, d'obrir
    /// ⚙ Configuració...). El motor no el dona mai: només diu si n'hi ha (hasPin) i si un PIN és correcte.
    /// </summary>
    public string? ProfilePin { get; set; }

    public const int MinPinLength = 4;
    public const int MaxPinLength = 8;

    /// <summary>Un PIN és de 4 a 8 xifres (0-9).</summary>
    public static bool IsValidPin(string pin) =>
        pin.Length is >= MinPinLength and <= MaxPinLength && pin.All(c => c is >= '0' and <= '9');

    // --- Del perfil: dispositius, enregistradors, distribució i programa. Del perfil, també les últimes llistes. ---
    // --- De l'ordinador (config.json): KnownDevices, UiZoom, EditorDevice i l'historial d'enregistraments. ---

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

    // --- Enregistraments recents (fase 5, pas 11a; de l'ordinador, com la llista) ---

    /// <summary>En tornar a obrir l'aplicació, es treuen de Recents els desats i els no trobats (per defecte, no).</summary>
    public bool ClearRecentsOnExit { get; set; }

    // --- Historial d'enregistraments (fase 5, pas 10a; de l'ordinador: depèn dels discos) ---

    /// <summary>Es fa una còpia de cada enregistrament original (per defecte, sí).</summary>
    public bool RecordingArchiveEnabled { get; set; } = true;

    /// <summary>Carpeta de l'historial; null = la de per defecte (<see cref="RecordingArchive.DefaultFolder"/>).</summary>
    public string? RecordingArchiveFolder { get; set; }

    /// <summary>Dies que es guarden (0 = no esborrar mai).</summary>
    public int RecordingArchiveKeepDays { get; set; } = RecordingArchive.DefaultKeepDays;

    /// <summary>Carpeta de l'historial (l'arrel, amb una subcarpeta per perfil), o null si no se'n fan còpies.</summary>
    public string? EffectiveRecordingArchiveFolder =>
        RecordingArchiveEnabled ? RecordingArchiveFolder ?? RecordingArchive.DefaultFolder : null;

    /// <summary>On es fan les còpies del perfil actiu (D-088), o null si no se'n fan.</summary>
    public string? ProfileRecordingArchiveFolder =>
        EffectiveRecordingArchiveFolder is { } root ? RecordingArchive.ProfileFolder(root, ProfileName) : null;

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

    /// <summary>Amagar el mesurador dels enregistradors que no enregistren (fase 5, pas 14c; del perfil). Per defecte, no.</summary>
    public bool HideIdleRecorderMeters { get; set; }

    /// <summary>
    /// Ordre dels reproductors a la pantalla, d'esquerra a dreta (fase 5, pas 16a; del perfil): identificadors.
    /// Buida = per número. Els que no hi surten van al final, per número.
    /// </summary>
    public List<string> PlayerOrder { get; set; } = [];

    /// <summary>Ordre dels enregistradors a la graella (fase 5, pas 16a; del perfil). Com <see cref="PlayerOrder"/>.</summary>
    public List<string> RecorderOrder { get; set; } = [];

    /// <summary>
    /// Ordre vàlid: identificadors de 1 a <paramref name="max"/> escrits com els fa el motor («1», «2»…), sense repetir-ne cap.
    /// </summary>
    public static bool IsValidOrder(IReadOnlyList<string> ids, int max) =>
        ids.Count <= max
        && ids.Distinct(StringComparer.Ordinal).Count() == ids.Count
        && ids.All(id => int.TryParse(id, out int n) && n >= 1 && n <= max && n.ToString() == id);

    /// <summary>Nom del programa que es fa (p. ex. «Magazín dels dissabtes»). Surt a la barra superior i a l'historial. Buit = cap.</summary>
    public string ProgramName { get; set; } = "";

    public const int MaxProgramName = 60;

    // --- Compressor/limitador de cada reproductor (fase 4, pas 4b; del perfil) ---

    /// <summary>Ajustos del compressor/limitador de cada reproductor (clau: identificador). Sense, desactivat.</summary>
    public Dictionary<string, DynamicsSettings> PlayerDynamics { get; } = [];

    /// <summary>Fos entre pistes encadenades de cada reproductor, en ms (fase 5; D-077). Sense, 0 (cap fos).</summary>
    public Dictionary<string, int> PlayerCrossfadeMs { get; } = [];

    /// <summary>Presets propis del compressor/limitador, en l'ordre en què es van desar (D-069).</summary>
    public List<ProcessingPreset> ProcessingPresets { get; set; } = [];

    // --- Retall automàtic de silencis (fase 5, pas 8b; del perfil) ---

    /// <summary>Reproductors que retallen els silencis inicials i finals (per defecte, cap: D-079).</summary>
    public HashSet<string> TrimPlayers { get; } = [];

    /// <summary>Llindar del silenci (dBFS de pic): un de −40, −50, −60, −70 o −80.</summary>
    public int TrimThresholdDb { get; set; } = -60;

    /// <summary>Marge abans del primer so (ms), perquè l'atac quedi sencer.</summary>
    public int TrimLeadMs { get; set; } = 10;

    /// <summary>Marge després de l'últim so (ms), per a la cua.</summary>
    public int TrimTailMs { get; set; } = 50;

    // --- Limitador dels enregistradors (fase 5, pas 9a; del perfil) ---

    /// <summary>Enregistradors amb el limitador activat (per defecte, cap).</summary>
    public HashSet<string> LimitedRecorders { get; } = [];

    /// <summary>Sostre del limitador, comú a tots els enregistradors (dBFS).</summary>
    public double RecorderLimiterCeilingDb { get; set; } = Recorder.DefaultLimiterCeilingDb;

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
    public const int MaxRecorders = 6; // fase 5, pas 14a (D-101): abans 4
    public const int MaxEndWarningSeconds = 60;
}
