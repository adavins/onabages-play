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
    /// <param name="cart">La botonera (opcional: sense, el camp no hi és).</param>
    public static JsonObject Event(IEnumerable<Player> players, IEnumerable<Recorder> recorders, CuePlayer? cue = null, CartWall? cart = null)
    {
        var playerLevels = new JsonObject();
        // Abaixada del compressor/limitador de cada reproductor (dB, ≥ 0), per a l'indicador de reducció.
        var reductions = new JsonObject();
        foreach (var p in players)
        {
            playerLevels[p.Id] = Levels(p.TakePeaks());
            reductions[p.Id] = Math.Round(p.TakeGainReductionDb(), 1);
        }

        var recorderLevels = new JsonObject();
        // Abaixada del limitador de cada enregistrador (dB, ≥ 0; 0 si no s'enregistra).
        var recorderReductions = new JsonObject();
        foreach (var r in recorders)
        {
            recorderLevels[r.Id] = Levels(r.Meter.Take());
            recorderReductions[r.Id] = Math.Round(r.TakeLimiterReductionDb(), 1);
        }

        var ev = Messages.Event("meters");
        ev["players"] = playerLevels;
        ev["playerReduction"] = reductions;
        ev["recorders"] = recorderLevels;
        ev["recorderReduction"] = recorderReductions;
        if (cue is not null)
            ev["cue"] = Levels(cue.TakePeaks());
        if (cart is not null)
            ev["cart"] = Levels(cart.Meter.Take());
        return ev;
    }

    private static JsonArray Levels((float Left, float Right) peaks) =>
        [PeakMeter.ToDb(peaks.Left), PeakMeter.ToDb(peaks.Right)];
}
