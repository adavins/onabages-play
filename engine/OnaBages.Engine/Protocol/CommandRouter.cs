using System.Text.Json;
using System.Text.Json.Nodes;

namespace OnaBages.Engine.Protocol;

/// <summary>
/// Rep el text d'una ordre, la valida i en retorna la resposta.
/// Totes les ordres noves s'afegeixen aquí (i a docs/protocol.md, i a les proves).
/// </summary>
public sealed class CommandRouter
{
    private readonly EngineState _state;
    private readonly Action _requestShutdown;

    /// <param name="state">Estat del motor.</param>
    /// <param name="requestShutdown">Acció que atura el motor (s'executa poc després de respondre).</param>
    public CommandRouter(EngineState state, Action requestShutdown)
    {
        _state = state;
        _requestShutdown = requestShutdown;
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
                _ => null,
            };

            return result is null
                ? Messages.Error(id, "unknown_command", $"Ordre desconeguda: {cmd}")
                : Messages.Ok(id, result);
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

    private static int? ReadInt(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out int i) ? i : null;

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? s) ? s : null;
}
