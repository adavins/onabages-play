using System.Text.Json.Nodes;
using OnaBages.Audio;

namespace OnaBages.Engine.Protocol;

/// <summary>
/// Conversió dels reproductors al format del protocol (docs/protocol.md, 4.2 i 5.2).
/// Els temps van en segons, amb tres decimals.
/// </summary>
public static class PlayerJson
{
    public static JsonObject Player(Player player) => new()
    {
        ["id"] = player.Id,
        ["state"] = State(player.State),
        ["deviceId"] = player.DeviceId,
        ["file"] = player.FilePath,
        ["duration"] = Seconds(player.Duration),
        ["position"] = Seconds(player.Position),
        ["error"] = player.ErrorMessage,
        ["underruns"] = player.Underruns,
        ["volumeDb"] = player.VolumeDb,
    };

    public static JsonArray All(IEnumerable<Player> players) =>
        new(players.Select(p => (JsonNode)Player(p)).ToArray());

    /// <summary>Esdeveniment <c>player.state</c>: un reproductor ha canviat.</summary>
    public static JsonObject StateEvent(Player player)
    {
        var ev = Messages.Event("player.state");
        ev["player"] = Player(player);
        return ev;
    }

    /// <summary>Esdeveniment <c>player.position</c>: posició de tots els reproductors.</summary>
    public static JsonObject PositionEvent(IEnumerable<Player> players)
    {
        var list = new JsonArray();
        foreach (var p in players)
        {
            list.Add(new JsonObject
            {
                ["id"] = p.Id,
                ["position"] = Seconds(p.Position),
                ["duration"] = Seconds(p.Duration),
            });
        }

        var ev = Messages.Event("player.position");
        ev["players"] = list;
        return ev;
    }

    public static string State(PlayerState state) => state switch
    {
        PlayerState.Empty => "empty",
        PlayerState.Ready => "ready",
        PlayerState.Playing => "playing",
        PlayerState.Paused => "paused",
        PlayerState.Ended => "ended",
        _ => "error",
    };

    private static double Seconds(TimeSpan time) => Math.Round(time.TotalSeconds, 3);
}
