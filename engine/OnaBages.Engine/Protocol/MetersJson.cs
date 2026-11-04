using System.Text.Json.Nodes;
using OnaBages.Audio;

namespace OnaBages.Engine.Protocol;

/// <summary>Esdeveniment <c>meters</c>: nivells de pic de tots els reproductors, enregistradors i la preescolta (docs/protocol.md, 5.2).</summary>
public static class MetersJson
{
    /// <summary>
    /// Recull el pic de cada element des de l'última crida (i el posa a zero).
    /// Valors en dBFS, amb un mínim de −90 (silenci).
    /// </summary>
    /// <param name="cue">La preescolta (opcional: sense, el camp no hi és).</param>
    public static JsonObject Event(IEnumerable<Player> players, IEnumerable<Recorder> recorders, CuePlayer? cue = null)
    {
        var playerLevels = new JsonObject();
        foreach (var p in players)
            playerLevels[p.Id] = Levels(p.TakePeaks());

        var recorderLevels = new JsonObject();
        foreach (var r in recorders)
            recorderLevels[r.Id] = Levels(r.Meter.Take());

        var ev = Messages.Event("meters");
        ev["players"] = playerLevels;
        ev["recorders"] = recorderLevels;
        if (cue is not null)
            ev["cue"] = Levels(cue.TakePeaks());
        return ev;
    }

    private static JsonArray Levels((float Left, float Right) peaks) =>
        [PeakMeter.ToDb(peaks.Left), PeakMeter.ToDb(peaks.Right)];
}
