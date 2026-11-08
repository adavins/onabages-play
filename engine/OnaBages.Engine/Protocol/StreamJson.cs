using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine.Data;

namespace OnaBages.Engine.Protocol;

/// <summary>Conversió de l'emissor d'streaming al format del protocol (docs/protocol.md, 4.16; fase 5, pas 31c).</summary>
public static class StreamJson
{
    /// <summary>L'emissor i la llista de servidors del perfil. Les contrasenyes no hi surten mai (només <c>hasPassword</c>).</summary>
    public static JsonObject Stream(Streamer s, IReadOnlyList<StreamServerSettings> servers, string? serverId) => new()
    {
        ["state"] = State(s.State),
        ["deviceId"] = s.DeviceId,
        ["inputActive"] = s.InputActive,
        ["serverId"] = serverId,
        ["servers"] = new JsonArray(servers.Select(x => (JsonNode)Server(x)).ToArray()),
        ["bitrateKbps"] = s.BitrateKbps,
        ["sampleRate"] = s.SampleRate,
        ["mono"] = s.Mono,
        ["title"] = s.Title,
        ["titleError"] = s.TitleError,
        ["gainDb"] = s.GainDb,
        ["leveler"] = new JsonObject { ["enabled"] = s.LevelerEnabled, ["gainDb"] = Round(s.LevelerGainDb) },
        ["limiter"] = new JsonObject { ["enabled"] = s.LimiterEnabled, ["ceilingDb"] = s.LimiterCeilingDb },
        ["processing"] = ConfigRepository.WriteDynamics(null, s.Dynamics),
        ["error"] = s.ErrorMessage,
        ["errorDetail"] = s.ErrorDetail,
        ["connectionError"] = s.ConnectionError,
        ["startedAt"] = s.StartedAt?.ToString("O"),
        ["connectedAt"] = s.ConnectedAt?.ToString("O"),
        ["progress"] = Progress(s),
        ["options"] = new JsonObject
        {
            ["bitrates"] = new JsonArray(Streamer.Bitrates.Select(b => (JsonNode)b).ToArray()),
            ["sampleRates"] = new JsonArray(Streamer.SampleRates.Select(r => (JsonNode)r).ToArray()),
            ["minGainDb"] = Streamer.MinGainDb,
            ["maxGainDb"] = Streamer.MaxGainDb,
            ["maxServers"] = EngineConfig.MaxStreamServers,
            ["maxTitle"] = EngineConfig.MaxStreamTitle,
        },
    };

    /// <summary>Un servidor de la llista, sense la contrasenya.</summary>
    public static JsonObject Server(StreamServerSettings s) => new()
    {
        ["id"] = s.Id,
        ["name"] = s.Name,
        ["type"] = ConfigRepository.StreamServerTypeName(s.Type),
        ["host"] = s.Host,
        ["port"] = s.Port,
        ["mount"] = s.Mount,
        ["user"] = s.User,
        ["streamId"] = s.StreamId,
        ["hasPassword"] = s.Password.Length > 0,
    };

    /// <summary>Temps, bytes i reconnexions de l'emissió en curs (o de l'última).</summary>
    public static JsonObject Progress(Streamer s) => new()
    {
        ["elapsed"] = s.StartedAt is { } started ? Math.Round((DateTimeOffset.Now - started).TotalSeconds, 1) : 0,
        ["connectedSeconds"] = s.ConnectedAt is { } connected ? Math.Round((DateTimeOffset.Now - connected).TotalSeconds, 1) : 0,
        ["bytesSent"] = s.BytesSent,
        ["bytesDropped"] = s.BytesDropped,
        ["samplesLost"] = s.SamplesLost,
        ["reconnects"] = s.Reconnects,
        ["levelerGainDb"] = Round(s.LevelerGainDb),
    };

    /// <summary>Esdeveniment <c>stream.state</c>: l'emissor o la llista de servidors ha canviat.</summary>
    public static JsonObject StateEvent(Streamer s, IReadOnlyList<StreamServerSettings> servers, string? serverId)
    {
        var ev = Messages.Event("stream.state");
        ev["stream"] = Stream(s, servers, serverId);
        return ev;
    }

    /// <summary>Esdeveniment <c>stream.progress</c>: un cop per segon mentre s'emet.</summary>
    public static JsonObject ProgressEvent(Streamer s)
    {
        var ev = Messages.Event("stream.progress");
        ev["progress"] = Progress(s);
        return ev;
    }

    public static string State(StreamerState state) => state switch
    {
        StreamerState.Idle => "idle",
        StreamerState.Connecting => "connecting",
        StreamerState.Streaming => "streaming",
        StreamerState.Reconnecting => "reconnecting",
        _ => "error",
    };

    private static double? Round(double? value) => value is { } v ? Math.Round(v, 1) : null;
}
