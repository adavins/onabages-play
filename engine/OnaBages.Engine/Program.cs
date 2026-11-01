using System.Net;
using System.Text;
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

var state = new EngineState();
var hub = new ClientHub(state, log);
var router = new CommandRouter(state, () => app.Lifetime.StopApplication());

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
