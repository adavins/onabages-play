using System.Diagnostics;
using System.Text.Json.Nodes;

namespace OnaBages.Engine;

/// <summary>
/// Estat global del motor. El motor és la font de veritat: la interfície
/// el demana amb <c>state.get</c> en connectar-se.
/// A la fase 1 s'hi afegiran els reproductors i els enregistradors.
/// </summary>
public sealed class EngineState
{
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private int _clients;

    public double UptimeSeconds => Math.Round(_uptime.Elapsed.TotalSeconds, 1);

    public int Clients => Volatile.Read(ref _clients);

    public void SetClients(int count) => Volatile.Write(ref _clients, count);

    public JsonObject ToJson() => new()
    {
        ["version"] = EngineInfo.Version,
        ["uptime"] = UptimeSeconds,
        ["clients"] = Clients,
    };
}
