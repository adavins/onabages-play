using OnaBages.Audio;

namespace OnaBages.Engine.Data;

/// <summary>Dispositiu desat a la configuració: identificador de Windows i nom (per si l'identificador canvia).</summary>
public sealed record DeviceRef(string Id, string Name);

/// <summary>
/// Contingut de <c>config.json</c>. Només el llegeix i l'escriu <see cref="ConfigRepository"/>.
/// </summary>
public sealed class EngineConfig
{
    /// <summary>Sortida que vol cada reproductor (clau: identificador del reproductor).</summary>
    public Dictionary<string, DeviceRef> Players { get; } = [];

    /// <summary>Entrada que vol cada enregistrador (clau: identificador de l'enregistrador).</summary>
    public Dictionary<string, DeviceRef> Recorders { get; } = [];

    /// <summary>Dispositius que hi havia l'última vegada: serveix per detectar els nous i els formats canviats.</summary>
    public List<AudioDevice> KnownDevices { get; set; } = [];

    /// <summary>Escala de la interfície (1 = 100 %). Entre <see cref="MinZoom"/> i <see cref="MaxZoom"/>.</summary>
    public double UiZoom { get; set; } = 1.0;

    public const double MinZoom = 0.7;
    public const double MaxZoom = 1.5;
}
