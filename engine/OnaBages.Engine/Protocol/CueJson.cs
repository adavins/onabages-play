using System.Text.Json.Nodes;
using OnaBages.Audio;

namespace OnaBages.Engine.Protocol;

/// <summary>Conversió de la preescolta al format del protocol (docs/protocol.md, 4.8).</summary>
public static class CueJson
{
    public static JsonObject Cue(EngineState state)
    {
        var cue = state.Cue;
        return new JsonObject
        {
            ["state"] = State(cue.State),
            ["deviceId"] = cue.DeviceId,
            ["file"] = cue.FilePath,
            ["duration"] = Seconds(cue.Duration),
            ["position"] = Seconds(cue.Position),
            ["volumeDb"] = cue.VolumeDb,
            ["format"] = cue.Format,
            ["error"] = cue.ErrorMessage,
            ["sharesPlayerOutput"] = state.CueSharesPlayerOutput,
        };
    }

    /// <summary>Esdeveniment <c>cue.state</c>: ha canviat l'estat, la pista, la sortida, el volum o l'error.</summary>
    public static JsonObject StateEvent(EngineState state)
    {
        var ev = Messages.Event("cue.state");
        ev["cue"] = Cue(state);
        return ev;
    }

    /// <summary>Esdeveniment <c>cue.position</c>: posició mentre sona (10 per segon).</summary>
    public static JsonObject PositionEvent(CuePlayer cue)
    {
        var ev = Messages.Event("cue.position");
        ev["position"] = Seconds(cue.Position);
        ev["duration"] = Seconds(cue.Duration);
        ev["format"] = cue.Format; // es coneix uns instants després de començar (en acabar l'anàlisi)
        return ev;
    }

    public static string State(CueState state) => state switch
    {
        CueState.Playing => "playing",
        CueState.Error => "error",
        _ => "stopped",
    };

    private static double Seconds(TimeSpan time) => Math.Round(time.TotalSeconds, 3);
}
