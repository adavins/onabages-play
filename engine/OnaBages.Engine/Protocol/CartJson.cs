using System.Text.Json.Nodes;
using OnaBages.Audio;

namespace OnaBages.Engine.Protocol;

/// <summary>Conversió de la botonera al format del protocol (docs/protocol.md, «Botonera»; fase 5, pas 28a).</summary>
public static class CartJson
{
    public static JsonObject Cart(EngineState state)
    {
        var cart = state.Cart;
        return new JsonObject
        {
            ["deviceId"] = cart.DeviceId,
            ["outputReady"] = cart.OutputReady,
            ["error"] = cart.ErrorMessage,
            ["sharesPlayerOutput"] = state.CartSharesPlayerOutput,
            ["pages"] = CartWall.Pages,
            ["rows"] = CartWall.Rows,
            ["columns"] = CartWall.Columns,
            ["maxVoices"] = CartWall.MaxVoices,
            ["minGainDb"] = CartWall.MinGainDb,
            ["maxGainDb"] = CartWall.MaxGainDb,
            ["buttons"] = new JsonArray(cart.Buttons.Select(b => (JsonNode)new JsonObject
            {
                ["page"] = b.Page,
                ["index"] = b.Index,
                ["path"] = b.Path,
                ["name"] = b.Name,
                ["color"] = b.Color,
                ["gainDb"] = b.GainDb,
                ["status"] = Status(b.Status),
                ["duration"] = b.Status == CartButtonStatus.Ready ? Seconds(b.Duration) : null,
                ["error"] = b.ErrorMessage,
            }).ToArray()),
            ["playing"] = Playing(cart),
        };
    }

    /// <summary>Esdeveniment <c>cart.state</c>: ha canviat un botó, la sortida, un error o què sona.</summary>
    public static JsonObject StateEvent(EngineState state)
    {
        var ev = Messages.Event("cart.state");
        ev["cart"] = Cart(state);
        return ev;
    }

    /// <summary>Esdeveniment <c>cart.position</c>: posició de cada tall que sona (10 per segon, només mentre en sona algun).</summary>
    public static JsonObject PositionEvent(CartWall cart)
    {
        var ev = Messages.Event("cart.position");
        ev["playing"] = Playing(cart);
        return ev;
    }

    private static JsonArray Playing(CartWall cart) => new(cart.Playing.Select(p => (JsonNode)new JsonObject
    {
        ["page"] = p.Button.Page,
        ["index"] = p.Button.Index,
        ["position"] = Seconds(p.Position),
        ["duration"] = Seconds(p.Button.Duration),
    }).ToArray());

    public static string Status(CartButtonStatus status) => status switch
    {
        CartButtonStatus.Ready => "ready",
        CartButtonStatus.Missing => "missing",
        CartButtonStatus.Error => "error",
        _ => "loading",
    };

    private static double Seconds(TimeSpan time) => Math.Round(time.TotalSeconds, 3);
}
