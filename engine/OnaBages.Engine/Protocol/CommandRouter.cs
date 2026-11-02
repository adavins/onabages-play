using System.Text.Json;
using System.Text.Json.Nodes;
using OnaBages.Audio;

namespace OnaBages.Engine.Protocol;

/// <summary>
/// Rep el text d'una ordre, la valida i en retorna la resposta.
/// Totes les ordres noves s'afegeixen aquí (i a docs/protocol.md, i a les proves).
/// </summary>
public sealed class CommandRouter
{
    private readonly EngineState _state;
    private readonly Action _requestShutdown;
    private readonly Func<IReadOnlyList<AudioDevice>> _listDevices;

    /// <param name="state">Estat del motor.</param>
    /// <param name="requestShutdown">Acció que atura el motor (s'executa poc després de respondre).</param>
    /// <param name="listDevices">
    /// Funció que retorna els dispositius d'àudio. Per defecte, els reals de Windows;
    /// les proves hi passen una llista inventada.
    /// </param>
    public CommandRouter(EngineState state, Action requestShutdown, Func<IReadOnlyList<AudioDevice>>? listDevices = null)
    {
        _state = state;
        _requestShutdown = requestShutdown;
        _listDevices = listDevices ?? DeviceList.GetAll;
    }

    public JsonObject Handle(string text)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return Messages.Error(null, "bad_json", "El missatge no és JSON vàlid.");
        }

        if (node is not JsonObject msg)
            return Messages.Error(null, "bad_json", "El missatge ha de ser un objecte JSON.");

        JsonNode? id = msg["id"];

        if (ReadInt(msg["v"]) != Messages.ProtocolVersion)
            return Messages.Error(id, "protocol_version",
                $"Versió de protocol no compatible (s'esperava {Messages.ProtocolVersion}).");

        string? cmd = ReadString(msg["cmd"]);
        if (string.IsNullOrWhiteSpace(cmd))
            return Messages.Error(id, "missing_cmd", "Falta el camp 'cmd'.");

        try
        {
            JsonObject? result = cmd switch
            {
                "engine.hello" => Hello(),
                "engine.ping" => Ping(),
                "state.get" => StateGet(),
                "engine.shutdown" => Shutdown(),
                "devices.list" => DevicesList(msg),
                _ => null,
            };

            return result is null
                ? Messages.Error(id, "unknown_command", $"Ordre desconeguda: {cmd}")
                : Messages.Ok(id, result);
        }
        catch (BadParamsException ex)
        {
            return Messages.Error(id, "bad_params", ex.Message);
        }
        catch (Exception ex)
        {
            // Un error en una ordre mai no ha d'aturar el motor.
            return Messages.Error(id, "internal", ex.Message);
        }
    }

    private static JsonObject Hello() => new()
    {
        ["name"] = EngineInfo.Name,
        ["version"] = EngineInfo.Version,
        ["protocol"] = Messages.ProtocolVersion,
    };

    private static JsonObject Ping() => new()
    {
        ["pong"] = true,
        ["time"] = DateTimeOffset.UtcNow.ToString("O"),
    };

    private JsonObject StateGet() => new()
    {
        ["engine"] = _state.ToJson(),
    };

    private JsonObject Shutdown()
    {
        // Fase 1+: rebutjar si hi ha enregistraments actius, tret de "force": true.
        _ = Task.Run(async () =>
        {
            await Task.Delay(200); // deixa temps perquè la resposta arribi al client
            _requestShutdown();
        });

        return new JsonObject { ["stopping"] = true };
    }

    /// <summary>
    /// Llista de dispositius d'àudio.
    /// Paràmetres opcionals: "kind" ("output", "input" o "all") i "includeInactive" (bool).
    /// </summary>
    private JsonObject DevicesList(JsonObject msg)
    {
        string kind = ReadString(msg["kind"]) ?? "all";
        if (kind is not ("all" or "output" or "input"))
            throw new BadParamsException("'kind' ha de ser \"output\", \"input\" o \"all\".");

        bool includeInactive = msg["includeInactive"] is JsonValue v && v.TryGetValue(out bool b) && b;

        var list = new JsonArray();
        foreach (var device in _listDevices())
        {
            if (kind == "output" && device.Kind != DeviceKind.Output) continue;
            if (kind == "input" && device.Kind != DeviceKind.Input) continue;
            if (!includeInactive && device.Status != DeviceStatus.Active) continue;

            list.Add(DeviceJson.Device(device));
        }

        return new JsonObject { ["devices"] = list };
    }

    /// <summary>Error en els paràmetres d'una ordre: es respon amb el codi "bad_params".</summary>
    private sealed class BadParamsException(string message) : Exception(message);

    private static int? ReadInt(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out int i) ? i : null;

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? s) ? s : null;
}
