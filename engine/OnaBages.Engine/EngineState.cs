using System.Diagnostics;
using System.Text.Json.Nodes;
using OnaBages.Audio;

namespace OnaBages.Engine;

/// <summary>
/// Estat global del motor. El motor és la font de veritat: la interfície
/// el demana amb <c>state.get</c> en connectar-se.
/// Fase 1: reproductors (fixos, sense configuració desada). Després: enregistradors.
/// </summary>
public sealed class EngineState
{
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private int _clients;

    /// <param name="players">Reproductors del motor. Les proves en poden passar de falsos, o cap.</param>
    public EngineState(IEnumerable<Player>? players = null)
    {
        Players = (players ?? []).ToList();
    }

    /// <summary>Reproductors, en ordre ("1", "2"...).</summary>
    public IReadOnlyList<Player> Players { get; }

    /// <summary>Reproductor amb aquest identificador, o null si no existeix.</summary>
    public Player? FindPlayer(string? id) => Players.FirstOrDefault(p => p.Id == id);

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
