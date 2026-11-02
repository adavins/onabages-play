using System.Text.Json.Nodes;
using OnaBages.Audio;

namespace OnaBages.Engine.Protocol;

/// <summary>
/// Conversió dels dispositius i dels seus canvis al format del protocol (docs/protocol.md, 4.1 i 5).
/// </summary>
public static class DeviceJson
{
    public static JsonObject Device(AudioDevice device) => new()
    {
        ["id"] = device.Id,
        ["name"] = device.Name,
        ["kind"] = device.Kind == DeviceKind.Output ? "output" : "input",
        ["status"] = Status(device.Status),
        ["format"] = Format(device.Format),
    };

    /// <summary>Esdeveniment <c>devices.changed</c> amb la llista de canvis.</summary>
    public static JsonObject ChangedEvent(IEnumerable<DeviceChange> changes)
    {
        var list = new JsonArray();
        foreach (var change in changes)
        {
            var item = new JsonObject
            {
                ["change"] = change.Type switch
                {
                    DeviceChangeType.Added => "added",
                    DeviceChangeType.Removed => "removed",
                    DeviceChangeType.StatusChanged => "status",
                    DeviceChangeType.Renamed => "renamed",
                    _ => "format",
                },
                ["device"] = Device(change.Device),
            };

            if (change.Type == DeviceChangeType.StatusChanged && change.PreviousStatus is { } status)
                item["previousStatus"] = Status(status);
            if (change.Type == DeviceChangeType.Renamed)
                item["previousName"] = change.PreviousName;
            if (change.Type == DeviceChangeType.FormatChanged)
                item["previousFormat"] = Format(change.PreviousFormat);

            list.Add(item);
        }

        var ev = Messages.Event("devices.changed");
        ev["changes"] = list;
        return ev;
    }

    private static string Status(DeviceStatus status) => status switch
    {
        DeviceStatus.Active => "active",
        DeviceStatus.Disabled => "disabled",
        _ => "unplugged",
    };

    private static JsonObject? Format(DeviceFormat? format) => format is null ? null : new JsonObject
    {
        ["sampleRate"] = format.SampleRate,
        ["bits"] = format.Bits,
        ["channels"] = format.Channels,
    };
}
