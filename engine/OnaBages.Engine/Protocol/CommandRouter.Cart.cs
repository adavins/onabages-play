using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OnaBages.Audio;
using OnaBages.Engine.Data;

namespace OnaBages.Engine.Protocol;

/// <summary>
/// Ordres de la botonera d'instantanis (fase 5, pas 28a; D-127). Els botons es diuen amb <c>page</c> (0, 1...) i
/// <c>index</c> (0, 1... dins de la pàgina, per files). Els canvis de botons i la sortida es desen al perfil.
/// </summary>
public sealed partial class CommandRouter
{
    private JsonObject CartResult() => new() { ["cart"] = CartJson.Cart(_state) };

    /// <summary>"deviceId": sortida de la botonera (pot ser la d'un reproductor). Si sonava alguna cosa, s'atura.</summary>
    private JsonObject CartSetDevice(JsonObject msg)
    {
        string deviceId = RequireString(msg, "deviceId");
        if (!_listDevices().Any(d => d.Id == deviceId && d.Kind == DeviceKind.Output))
            throw new BadParamsException("No hi ha cap sortida amb aquest identificador.");
        _state.Cart.SetDevice(deviceId);
        _hardware?.Remember(ElementKind.Cart, EngineState.CartId, deviceId, _listDevices());
        return CartResult();
    }

    /// <summary>"page", "index", "path" (ruta completa) i "name" opcional. Si el botó ja tenia un fitxer, el substitueix.</summary>
    private JsonObject CartAssign(JsonObject msg)
    {
        var (page, index) = CartSlot(msg);
        string path = RequireString(msg, "path");
        if (!Path.IsPathFullyQualified(path))
            throw new BadParamsException("'path' ha de ser una ruta completa.");
        _state.Cart.Assign(page, index, path, CartName(msg["name"]));
        SaveCartButtons();
        return CartResult();
    }

    /// <summary>"page", "index" i, opcionals, "name", "color" ("#rrggbb", o "" per treure'l) i "gainDb" (de −30 a +10).</summary>
    private JsonObject CartConfigure(JsonObject msg)
    {
        var (page, index) = CartSlot(msg);
        string? name = CartName(msg["name"]);
        string? color = null;
        bool clearColor = false;
        if (msg["color"] is { } colorNode)
        {
            color = ReadString(colorNode) ?? throw new BadParamsException("'color' ha de ser un text (p. ex. \"#d63384\").");
            if (color.Length == 0) clearColor = true;
            else if (!Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$"))
                throw new BadParamsException("'color' ha de ser de la forma #rrggbb (p. ex. #d63384).");
        }
        double? gain = OptionalNumber(msg["gainDb"], "gainDb", CartWall.MinGainDb, CartWall.MaxGainDb);
        _state.Cart.Configure(page, index, name, clearColor ? null : color, gain, clearColor);
        SaveCartButtons();
        return CartResult();
    }

    /// <summary>"page", "index": treu el fitxer del botó (el fitxer no es toca).</summary>
    private JsonObject CartClear(JsonObject msg)
    {
        var (page, index) = CartSlot(msg);
        _state.Cart.Clear(page, index);
        SaveCartButtons();
        return CartResult();
    }

    /// <summary>"page", "index": si no sona, el fa sonar des del principi; si sona, l'atura. Resposta amb "playing".</summary>
    private JsonObject CartPress(JsonObject msg)
    {
        var (page, index) = CartSlot(msg);
        bool playing = _state.Cart.Press(page, index);
        var result = CartResult();
        result["playing"] = playing;
        return result;
    }

    private JsonObject CartStop(JsonObject msg)
    {
        var (page, index) = CartSlot(msg);
        _state.Cart.Stop(page, index);
        return CartResult();
    }

    private JsonObject CartStopAll()
    {
        _state.Cart.StopAll();
        return CartResult();
    }

    private static (int Page, int Index) CartSlot(JsonObject msg)
    {
        double page = RequireNumber(msg, "page", $"la pàgina (de 0 a {CartWall.Pages - 1})");
        double index = RequireNumber(msg, "index", $"el botó dins de la pàgina (de 0 a {CartWall.ButtonsPerPage - 1})");
        if (page != Math.Floor(page) || page is < 0 or >= CartWall.Pages)
            throw new BadParamsException($"'page' ha de ser de 0 a {CartWall.Pages - 1}.");
        if (index != Math.Floor(index) || index is < 0 or >= CartWall.ButtonsPerPage)
            throw new BadParamsException($"'index' ha de ser de 0 a {CartWall.ButtonsPerPage - 1}.");
        return ((int)page, (int)index);
    }

    private static string? CartName(JsonNode? node)
    {
        if (node is null) return null;
        string name = (ReadString(node) ?? throw new BadParamsException("'name' ha de ser un text.")).Trim();
        if (name.Length > CartWall.MaxNameLength)
            throw new BadParamsException($"El nom del botó pot tenir com a molt {CartWall.MaxNameLength} caràcters.");
        return name.Length == 0 ? null : name;
    }

    /// <summary>Desa tots els botons al perfil (només les rutes i els ajustos).</summary>
    private void SaveCartButtons()
    {
        var buttons = _state.Cart.Buttons
            .Select(b => new CartButtonSettings(b.Page, b.Index, b.Path, b.Name, b.Color, b.GainDb))
            .ToList();
        _config?.Update(c => c.CartButtons = buttons);
    }
}
