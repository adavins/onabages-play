using System.Text.Json.Nodes;

namespace OnaBages.Engine.Protocol;

/// <summary>
/// Construcció dels missatges del protocol (vegeu docs/protocol.md).
/// </summary>
public static class Messages
{
    public const int ProtocolVersion = 1;

    /// <summary>Resposta correcta a una ordre.</summary>
    public static JsonObject Ok(JsonNode? id, JsonObject result) => new()
    {
        ["v"] = ProtocolVersion,
        ["id"] = id?.DeepClone(),
        ["ok"] = true,
        ["result"] = result,
    };

    /// <summary>Resposta d'error a una ordre.</summary>
    public static JsonObject Error(JsonNode? id, string code, string message) => new()
    {
        ["v"] = ProtocolVersion,
        ["id"] = id?.DeepClone(),
        ["ok"] = false,
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    };

    /// <summary>Esdeveniment del motor cap als clients. Afegeix-hi els camps després.</summary>
    public static JsonObject Event(string name) => new()
    {
        ["v"] = ProtocolVersion,
        ["ev"] = name,
    };
}
