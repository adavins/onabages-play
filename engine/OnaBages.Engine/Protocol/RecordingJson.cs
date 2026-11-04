using System.Text.Json.Nodes;

namespace OnaBages.Engine.Protocol;

/// <summary>Enregistraments acabats (pendents i recents) en el format del protocol (docs/protocol.md, 4.6).</summary>
public static class RecordingJson
{
    public static JsonObject Item(RecordingItem item) => new()
    {
        ["id"] = item.Id,
        ["path"] = item.Path,
        ["recorder"] = item.RecorderId,
        ["startedAt"] = item.StartedAt.ToString("O"),
        ["duration"] = Math.Round(item.Duration.TotalSeconds, 1),
        ["fileSize"] = item.FileSize,
        ["saved"] = item.Saved,
        ["busy"] = item.Busy,
        ["closedCleanly"] = item.ClosedCleanly,
        ["exists"] = item.Busy || File.Exists(item.Path),
        ["problem"] = item.Problem,
    };

    public static JsonArray All(RecordingList list) =>
        new(list.Items.Select(i => (JsonNode)Item(i)).ToArray());

    /// <summary>Esdeveniment <c>recordings.changed</c>: la llista sencera (com a molt 30 elements).</summary>
    public static JsonObject ChangedEvent(RecordingList list)
    {
        var ev = Messages.Event("recordings.changed");
        ev["recordings"] = All(list);
        return ev;
    }
}
