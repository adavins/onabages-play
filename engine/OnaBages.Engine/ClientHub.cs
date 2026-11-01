using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using OnaBages.Engine.Protocol;

namespace OnaBages.Engine;

/// <summary>
/// Gestiona els clients WebSocket connectats (la interfície, la consola de proves...):
/// rep ordres, envia respostes i difon esdeveniments a tothom.
/// </summary>
public sealed class ClientHub
{
    public const int MaxMessageBytes = 64 * 1024;

    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly EngineState _state;
    private readonly ILogger _log;

    public ClientHub(EngineState state, ILogger log)
    {
        _state = state;
        _log = log;
    }

    /// <summary>Atén un client fins que es desconnecta.</summary>
    public async Task RunClientAsync(WebSocket socket, CommandRouter router, CancellationToken ct)
    {
        var client = new Client(socket);
        _clients[client.Id] = client;
        _state.SetClients(_clients.Count);
        _log.LogInformation("Client connectat ({Count} en total)", _clients.Count);

        var buffer = new byte[4096];
        using var message = new MemoryStream();

        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (received.MessageType == WebSocketMessageType.Close)
                    break;

                message.Write(buffer, 0, received.Count);

                if (message.Length > MaxMessageBytes)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Missatge massa gran", ct);
                    break;
                }

                if (!received.EndOfMessage)
                    continue;

                if (received.MessageType == WebSocketMessageType.Text)
                {
                    string text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    JsonObject response = router.Handle(text);
                    await client.SendAsync(response, ct);
                }

                message.SetLength(0);
            }
        }
        catch (OperationCanceledException)
        {
            // El motor s'atura o el client ha marxat.
        }
        catch (WebSocketException ex)
        {
            _log.LogInformation("Connexió tancada de manera abrupta: {Message}", ex.Message);
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            _state.SetClients(_clients.Count);
            await client.TryCloseAsync();
            _log.LogInformation("Client desconnectat ({Count} en total)", _clients.Count);
        }
    }

    /// <summary>Envia un esdeveniment a tots els clients connectats.</summary>
    public async Task BroadcastAsync(JsonObject ev, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(ev.ToJsonString());
        foreach (var client in _clients.Values)
        {
            try
            {
                await client.SendAsync(bytes, ct);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
                // Aquest client ja no hi és; se'n traurà quan acabi el seu bucle.
            }
        }
    }

    private sealed class Client
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public Client(WebSocket socket) => Socket = socket;

        public Guid Id { get; } = Guid.NewGuid();

        public WebSocket Socket { get; }

        public Task SendAsync(JsonObject message, CancellationToken ct) =>
            SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), ct);

        public async Task SendAsync(byte[] bytes, CancellationToken ct)
        {
            // Un WebSocket no admet dos enviaments alhora: els posem en fila.
            await _sendLock.WaitAsync(ct);
            try
            {
                if (Socket.State == WebSocketState.Open)
                    await Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async Task TryCloseAsync()
        {
            try
            {
                if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Adéu", CancellationToken.None);
            }
            catch (Exception)
            {
                // Ja tancat.
            }
        }
    }
}
