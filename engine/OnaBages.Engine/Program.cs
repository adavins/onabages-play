using System.Net;
using System.Text;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Protocol;

// Punt d'entrada del motor d'Ona Bages Play.
// Ús: OnaBages.Engine [--port 47800] [--token <testimoni>]

Console.OutputEncoding = Encoding.UTF8;

EngineOptions options;
try
{
    options = EngineOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

var builder = WebApplication.CreateSlimBuilder();

// Només s'accepten connexions des del mateix ordinador.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.Port));

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

var app = builder.Build();
var log = app.Logger;

// Fase 1: dos reproductors fixos. Més endavant, el nombre sortirà de la configuració.
var state = new EngineState([new Player("1"), new Player("2")]);
var hub = new ClientHub(state, log);

// Envia un esdeveniment a tots els clients sense esperar (no pot bloquejar qui el crida).
void Broadcast(System.Text.Json.Nodes.JsonObject ev) => _ = Task.Run(async () =>
{
    try { await hub.BroadcastAsync(ev, app.Lifetime.ApplicationStopping); }
    catch (OperationCanceledException) { /* el motor s'està aturant */ }
});

foreach (var player in state.Players)
{
    player.StateChanged += p =>
    {
        if (p.State == PlayerState.Error)
            log.LogWarning("Reproductor {Id}: error — {Message} (detall: {Detail})", p.Id, p.ErrorMessage, p.ErrorDetail ?? "cap");
        else
            log.LogInformation("Reproductor {Id}: {State} {File}", p.Id, PlayerJson.State(p.State), Path.GetFileName(p.FilePath) ?? "");
        Broadcast(PlayerJson.StateEvent(p));
    };
}
var router = new CommandRouter(state, () => app.Lifetime.StopApplication());

// Vigilant de dispositius: avisa els clients quan es connecta o desconnecta alguna cosa.
var watcher = new DeviceWatcher();
watcher.Changed += changes =>
{
    foreach (var change in changes)
        log.LogInformation("Dispositiu: {Change}", DescribeChange(change));

    Broadcast(DeviceJson.ChangedEvent(changes));
};
watcher.Error += ex => log.LogWarning("Error en llegir els dispositius: {Message}", ex.Message);
try
{
    watcher.Start();
}
catch (Exception ex)
{
    // Sense avisos de dispositius el motor pot continuar; només ho deixem escrit.
    log.LogError("No s'han pogut activar els avisos de dispositius: {Message}", ex.Message);
}

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

// Comprovació de vida (sense testimoni): la fa servir Electron per saber si el motor ja funciona.
app.MapGet("/health", () => Results.Text("ok"));

// Canal principal amb la interfície.
app.Map("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    if (!EngineOptions.TokenMatches(ctx.Request.Query["token"].ToString(), options.Token))
    {
        log.LogWarning("Connexió rebutjada: testimoni incorrecte");
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await hub.RunClientAsync(socket, router, ctx.RequestAborted);
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    RuntimeFile.Write(options);
    Console.WriteLine($"{EngineInfo.Name} {EngineInfo.Version} (protocol {Messages.ProtocolVersion})");
    Console.WriteLine($"Escoltant a ws://127.0.0.1:{options.Port}/ws");
    Console.WriteLine($"Testimoni: {options.Token}");
    Console.WriteLine($"Fitxer d'execució: {RuntimeFile.FilePath}");
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    log.LogInformation("Aturant el motor");
    watcher.Dispose();
    foreach (var player in state.Players)
        player.Dispose();
    RuntimeFile.DeleteIfOwned();
});

// Esdeveniment periòdic d'estat: permet a la interfície saber que el motor és viu.
_ = Task.Run(async () =>
{
    var stopping = app.Lifetime.ApplicationStopping;
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
    try
    {
        while (await timer.WaitForNextTickAsync(stopping))
        {
            var ev = Messages.Event("engine.status");
            ev["uptime"] = state.UptimeSeconds;
            ev["clients"] = state.Clients;
            await hub.BroadcastAsync(ev, stopping);
        }
    }
    catch (OperationCanceledException)
    {
        // El motor s'està aturant.
    }
});

// Revisió dels reproductors (final de pista, errors) i posició, 10 vegades per segon.
// Cada reproductor es revisa per separat: un error en un no afecta els altres.
_ = Task.Run(async () =>
{
    var stopping = app.Lifetime.ApplicationStopping;
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
    try
    {
        while (await timer.WaitForNextTickAsync(stopping))
        {
            foreach (var player in state.Players)
            {
                try { player.Tick(); }
                catch (Exception ex) { log.LogError("Reproductor {Id}: error en revisar-lo: {Message}", player.Id, ex.Message); }
            }

            if (state.Players.Any(p => p.State == PlayerState.Playing))
                await hub.BroadcastAsync(PlayerJson.PositionEvent(state.Players), stopping);
        }
    }
    catch (OperationCanceledException)
    {
        // El motor s'està aturant.
    }
});

try
{
    await app.RunAsync();
}
catch (IOException ex)
{
    Console.Error.WriteLine($"No s'ha pogut obrir el port {options.Port}. Potser ja hi ha un motor en marxa. ({ex.Message})");
    return 1;
}

return 0;

// Text breu d'un canvi de dispositiu, per al registre del motor.
static string DescribeChange(DeviceChange change)
{
    string name = change.Device.Name;
    return change.Type switch
    {
        DeviceChangeType.Added => $"nou: {name}",
        DeviceChangeType.Removed => $"ja no hi és: {name}",
        DeviceChangeType.StatusChanged => $"{name}: {StatusText(change.PreviousStatus)} → {StatusText(change.Device.Status)}",
        DeviceChangeType.Renamed => $"reanomenat: «{change.PreviousName}» → «{name}»",
        _ => $"{name}: format canviat",
    };

    static string StatusText(DeviceStatus? status) => status switch
    {
        DeviceStatus.Active => "actiu",
        DeviceStatus.Disabled => "desactivat",
        DeviceStatus.Unplugged => "desconnectat",
        _ => "?",
    };
}
