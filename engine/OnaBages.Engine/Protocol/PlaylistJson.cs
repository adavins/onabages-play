using System.Text.Json.Nodes;
using OnaBages.Audio;

namespace OnaBages.Engine.Protocol;

/// <summary>Conversió de les llistes al format del protocol (docs/protocol.md, 4.5 i 5).</summary>
public static class PlaylistJson
{
    public static JsonObject Playlist(Player player)
    {
        var playlist = player.Playlist;
        var items = new JsonArray();
        foreach (var item in playlist.Items)
            items.Add(Item(item));

        return new JsonObject
        {
            ["player"] = player.Id,
            ["items"] = items,
            ["cueId"] = playlist.CueId,
            ["totalDuration"] = Math.Round(playlist.TotalDuration.TotalSeconds, 3),
        };
    }

    public static JsonObject Item(PlaylistItem item) => new()
    {
        ["id"] = item.Id,
        // "section": separador de secció (pas 9c). "name" és el títol; "path" és buit.
        ["type"] = item.IsSection ? "section" : "track",
        ["name"] = item.Name,
        ["path"] = item.Path,
        ["duration"] = item.Duration is { } d ? Math.Round(d.TotalSeconds, 3) : null,
        ["format"] = item.Format,
        // Sonoritat en LUFS (fase 4, pas 5b), quan ja s'ha mesurat; si no (o és silenci), null.
        ["loudness"] = item.LoudnessLufs is { } lufs ? Math.Round(lufs, 1) : null,
        // On hi ha so per a cada llindar (retall de silencis, fase 5): [[llindar, inici, final], ...] en segons; null si encara no s'ha analitzat.
        ["sound"] = item.Sound is null ? null : new JsonArray(item.Sound
            .Select(b => (JsonNode)new JsonArray(b.ThresholdDb, Math.Round(b.Start, 4), Math.Round(b.End, 4))).ToArray()),
        ["status"] = item.Status switch
        {
            TrackStatus.Analyzing => "analyzing",
            TrackStatus.Ok => "ok",
            TrackStatus.Error => "error",
            _ => "missing",
        },
        ["problem"] = item.Problem,
        ["loop"] = item.Loop,
        ["autoNext"] = item.AutoNext,
        ["color"] = item.Color,
        ["collapsed"] = item.Collapsed,
    };

    public static JsonArray All(IEnumerable<Player> players) =>
        new(players.Select(p => (JsonNode)Playlist(p)).ToArray());

    /// <summary>Esdeveniment <c>playlist.changed</c>: la llista sencera d'un reproductor.</summary>
    public static JsonObject ChangedEvent(Player player)
    {
        var ev = Messages.Event("playlist.changed");
        ev["playlist"] = Playlist(player);
        return ev;
    }

    /// <summary>Metadades d'un fitxer (ordre <c>track.info</c>).</summary>
    public static JsonObject Metadata(TrackMetadata m) => new()
    {
        ["path"] = m.Path,
        ["fileSize"] = m.FileSize,
        ["duration"] = Math.Round(m.Duration.TotalSeconds, 3),
        ["format"] = m.Format,
        ["title"] = m.Title,
        ["artist"] = m.Artist,
        ["album"] = m.Album,
        ["year"] = m.Year,
        ["genre"] = m.Genre,
        ["comment"] = m.Comment,
    };
}
