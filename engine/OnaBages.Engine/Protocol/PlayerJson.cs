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
        ["currentItemId"] = player.CurrentItemId,
        ["lastPlayedId"] = player.LastPlayedId,
        ["format"] = player.Format,
        // Compressor/limitador (fase 4, pas 4b; protocol 4.13).
        ["processing"] = Data.ConfigRepository.WriteDynamics(null, player.Dynamics),
        // Fos entre pistes encadenades (fase 5): 0 = cap.
        ["crossfadeMs"] = player.CrossfadeMs,
        // Fade-out en marxa (pas 20): en acabar, el reproductor s'atura (state «ended»).
        ["fadingOut"] = player.FadingOut,
        // Retall de silencis (fase 5): enabled i els ajustos (comuns al perfil).
        ["trim"] = new JsonObject
        {
            ["enabled"] = player.Trim.Enabled,
            ["thresholdDb"] = player.Trim.ThresholdDb,
            ["leadMs"] = player.Trim.LeadMs,
            ["tailMs"] = player.Trim.TailMs,
        },
        // Anivellador (fase 5, pas 30b): gainDb és el guany al punt que sona (null = cap). L'objectiu és el de la normalització.
        ["leveler"] = new JsonObject
        {
            ["enabled"] = player.Level,
            ["maxBoostDb"] = player.LevelerMaxBoostDb,
            ["gainDb"] = player.LevelerGainDb is { } lg ? Math.Round(lg, 1) : null,
        },
        // Normalització de sonoritat (fase 4, pas 5b): gainDb és el guany de la pista que sona (null = cap).
        ["normalization"] = new JsonObject
        {
            ["enabled"] = player.Normalize,
            ["targetLufs"] = player.NormalizationTargetLufs,
            ["gainDb"] = player.NormalizationDb,
        },
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
                ["levelerGainDb"] = p.LevelerGainDb is { } lg ? Math.Round(lg, 1) : null, // fase 5, D-135
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
