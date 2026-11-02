namespace OnaBages.Audio;

/// <summary>Tipus de canvi d'un dispositiu entre dues lectures.</summary>
public enum DeviceChangeType
{
    /// <summary>Apareix un dispositiu nou (p. ex. s'ha connectat un USB).</summary>
    Added,
    /// <summary>El dispositiu ja no hi és (p. ex. s'ha tret un USB o una targeta).</summary>
    Removed,
    /// <summary>Ha canviat d'estat: actiu, desactivat o desconnectat.</summary>
    StatusChanged,
    /// <summary>L'usuari l'ha reanomenat a Windows.</summary>
    Renamed,
    /// <summary>Ha canviat el format a Windows (p. ex. de 44,1 a 48 kHz).</summary>
    FormatChanged,
}

/// <summary>
/// Un canvi detectat. <see cref="Device"/> és el dispositiu tal com és ara
/// (o tal com era, si s'ha tret). Els camps «Previous» només es fan servir
/// en el tipus de canvi corresponent.
/// </summary>
public sealed record DeviceChange(
    DeviceChangeType Type,
    AudioDevice Device,
    DeviceStatus? PreviousStatus = null,
    string? PreviousName = null,
    DeviceFormat? PreviousFormat = null);

public static class DeviceChanges
{
    /// <summary>
    /// Compara dues lectures de la llista de dispositius i retorna les diferències.
    /// Els dispositius es reconeixen per l'identificador de Windows.
    /// Un mateix dispositiu pot tenir més d'un canvi (p. ex. estat i format).
    /// </summary>
    public static List<DeviceChange> Compare(IReadOnlyList<AudioDevice> before, IReadOnlyList<AudioDevice> after)
    {
        var changes = new List<DeviceChange>();
        var beforeById = before.ToDictionary(d => d.Id);
        var afterIds = after.Select(d => d.Id).ToHashSet();

        foreach (var now in after)
        {
            if (!beforeById.TryGetValue(now.Id, out var old))
            {
                changes.Add(new DeviceChange(DeviceChangeType.Added, now));
                continue;
            }

            if (old.Status != now.Status)
                changes.Add(new DeviceChange(DeviceChangeType.StatusChanged, now, PreviousStatus: old.Status));

            if (old.Name != now.Name)
                changes.Add(new DeviceChange(DeviceChangeType.Renamed, now, PreviousName: old.Name));

            if (old.Format != now.Format)
                changes.Add(new DeviceChange(DeviceChangeType.FormatChanged, now, PreviousFormat: old.Format));
        }

        foreach (var old in before)
        {
            if (!afterIds.Contains(old.Id))
                changes.Add(new DeviceChange(DeviceChangeType.Removed, old));
        }

        return changes;
    }
}
