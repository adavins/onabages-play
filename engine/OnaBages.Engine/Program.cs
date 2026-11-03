using System.Net;
using System.Text;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
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

// Fase 1: dos reproductors i dos enregistradors fixos. Més endavant, el nombre sortirà de la configuració.
var state = new EngineState([new Player("1"), new Player("2")], [new Recorder("1"), new Recorder("2")]);
var hub = new ClientHub(state, log);

// Envia un esdeveniment a tots els clients sense esperar (no pot bloquejar qui el crida).
void Broadcast(System.Text.Json.Nodes.JsonObject ev) => _ = Task.Run(async () =>
{
    try { await hub.BroadcastAsync(ev, app.Lifetime.ApplicationStopping); }
    catch (OperationCanceledException) { /* el motor s'està aturant */ }
});

foreach (var player in state.Players)
{
    string lastLogged = "";
    player.StateChanged += p =>
    {
        // Al registre només hi va el canvi d'estat o de pista (no cada moviment del fader).
        string summary = $"{p.State}|{p.FilePath}|{p.ErrorMessage}";
        if (summary != lastLogged)
        {
            lastLogged = summary;
            if (p.State == PlayerState.Error)
                log.LogWarning("Reproductor {Id}: error — {Message} (detall: {Detail})", p.Id, p.ErrorMessage, p.ErrorDetail ?? "cap");
            else
                log.LogInformation("Reproductor {Id}: {State} {File}", p.Id, PlayerJson.State(p.State), Path.GetFileName(p.FilePath) ?? "");
        }
        Broadcast(PlayerJson.StateEvent(p));
    };
}
foreach (var recorder in state.Recorders)
{
    recorder.StateChanged += r =>
    {
        if (r.State == RecorderState.Error)
            log.LogWarning("Enregistrador {Id}: error — {Message} (detall: {Detail})", r.Id, r.ErrorMessage, r.ErrorDetail ?? "cap");
        else
            log.LogInformation("Enregistrador {Id}: {State} {File}", r.Id, RecorderJson.State(r.State), r.FilePath ?? "");
        Broadcast(RecorderJson.StateEvent(r));
    };
}

// Configuració desada (config.json): dispositiu de cada element i ajustos de la interfície.
var config = new ConfigStore(new ConfigRepository());
config.Warning += message => log.LogWarning("Configuració: {Message}", message);
var hardware = new HardwareAssignments(state, config);
hardware.Warning += message => log.LogWarning("Maquinari: {Message}", message);

var router = new CommandRouter(state, () => app.Lifetime.StopApplication(), hardware: hardware, config: config);

// Vigilant de dispositius: avisa els clients quan es connecta o desconnecta alguna cosa.
var watcher = new DeviceWatcher();
watcher.Changed += changes =>
{
    foreach (var change in changes)
        log.LogInformation("Dispositiu: {Change}", DescribeChange(change));

    Broadcast(DeviceJson.ChangedEvent(changes));

    // Si ha tornat el dispositiu d'un element que l'esperava, s'hi torna a assignar.
    foreach (var check in hardware.DevicesChanged(watcher.Current))
    {
        log.LogInformation("{Element}: ha tornat «{Device}»; s'hi torna a assignar",
            HardwareAssignments.ElementName(check.Kind, check.ElementId), check.Device!.Name);
        Broadcast(HardwareJson.ReassignedEvent(check));
    }
};
watcher.Error += ex => log.LogWarning("Error en llegir els dispositius: {Message}", ex.Message);

IReadOnlyList<AudioDevice> devicesAtStart = [];
try
{
    watcher.Start();
    devicesAtStart = watcher.Current;
}
catch (Exception ex)
{
    // Sense avisos de dispositius el motor pot continuar; només ho deixem escrit.
    log.LogError("No s'han pogut activar els avisos de dispositius: {Message}", ex.Message);
    try { devicesAtStart = DeviceList.GetAll(); }
    catch (Exception ex2) { log.LogError("No s'han pogut llegir els dispositius: {Message}", ex2.Message); }
}

// Comparació del maquinari amb la configuració desada (especificació 5.6).
LogHardwareReport(hardware.Start(devicesAtStart));

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
    // Primer els enregistradors: tanquen els fitxers correctament (i esborren el marcador .rec).
    foreach (var recorder in state.Recorders)
    {
        if (recorder.State == RecorderState.Recording)
            log.LogInformation("Enregistrador {Id}: tancant {File}", recorder.Id, recorder.FilePath);
        recorder.Dispose();
    }
    foreach (var player in state.Players)
        player.Dispose();
    RuntimeFile.DeleteIfOwned();
});

// Mesuradors: 25 vegades per segon, tots els nivells en un sol missatge (especificació 4.4).
// Només si hi ha alguna interfície connectada.
_ = Task.Run(async () =>
{
    var stopping = app.Lifetime.ApplicationStopping;
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(40));
    try
    {
        while (await timer.WaitForNextTickAsync(stopping))
        {
            if (state.Clients == 0) continue;
            await hub.BroadcastAsync(MetersJson.Event(state.Players, state.Recorders), stopping);
        }
    }
    catch (OperationCanceledException)
    {
        // El motor s'està aturant.
    }
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

// Revisió dels reproductors i enregistradors (final de pista, errors), 10 vegades per segon,
// posició dels reproductors (10/s mentre sonen) i progrés dels enregistraments (1/s).
// Cada element es revisa per separat: un error en un no afecta els altres.
_ = Task.Run(async () =>
{
    var stopping = app.Lifetime.ApplicationStopping;
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
    int tick = 0;

    // Últim valor vist dels comptadors de problemes, per escriure al registre quan i on passa cada un.
    var lastGlitches = new Dictionary<Recorder, int>();
    var lastLost = new Dictionary<Recorder, long>();
    var lastUnderruns = new Dictionary<Player, int>();

    try
    {
        while (await timer.WaitForNextTickAsync(stopping))
        {
            tick++;
            foreach (var recorder in state.Recorders)
            {
                try { recorder.Tick(); }
                catch (Exception ex) { log.LogError("Enregistrador {Id}: error en revisar-lo: {Message}", recorder.Id, ex.Message); }

                int glitches = recorder.CaptureGlitches;
                int glitchesBefore = lastGlitches.GetValueOrDefault(recorder);
                if (glitches > glitchesBefore)
                    log.LogWarning("Enregistrador {Id}: Windows avisa que s'han perdut dades de captura (a {Elapsed} de l'enregistrament; {Total} en total)",
                        recorder.Id, recorder.Elapsed.ToString(@"hh\:mm\:ss\.f"), glitches);
                lastGlitches[recorder] = glitches; // també quan baixa: un enregistrament nou torna a començar de zero

                long lost = recorder.SamplesLost;
                long lostBefore = lastLost.GetValueOrDefault(recorder);
                if (lost > lostBefore)
                    log.LogWarning("Enregistrador {Id}: s'han perdut {Count} mostres perquè el disc no ha anat prou ràpid (a {Elapsed} de l'enregistrament)",
                        recorder.Id, lost - lostBefore, recorder.Elapsed.ToString(@"hh\:mm\:ss\.f"));
                lastLost[recorder] = lost;
            }

            if (tick % 10 == 0 && state.IsRecording)
                await hub.BroadcastAsync(RecorderJson.ProgressEvent(state.Recorders), stopping);

            foreach (var player in state.Players)
            {
                try { player.Tick(); }
                catch (Exception ex) { log.LogError("Reproductor {Id}: error en revisar-lo: {Message}", player.Id, ex.Message); }

                int underruns = player.Underruns;
                if (underruns > lastUnderruns.GetValueOrDefault(player))
                    log.LogWarning("Reproductor {Id}: tall, sense dades a temps (a {Position} de la pista; {Total} en total)",
                        player.Id, player.Position.ToString(@"hh\:mm\:ss\.f"), underruns);
                lastUnderruns[player] = underruns;
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

// Escriu al registre el resum de l'arrencada: una línia per element i per avís.
void LogHardwareReport(HardwareReport report)
{
    if (report.FirstRun)
        log.LogInformation("Maquinari: no hi ha cap dispositiu assignat (primera arrencada o configuració nova)");

    foreach (var check in report.Elements)
    {
        string element = HardwareAssignments.ElementName(check.Kind, check.ElementId);
        string saved = check.Saved.Name;
        switch (check.Result)
        {
            case CheckResult.Ok:
                log.LogInformation("{Element}: {Device}", element, check.Device!.Name);
                break;
            case CheckResult.Unplugged:
                log.LogWarning("{Element}: {Device} — desconnectat (sense cable); assignat igualment", element, check.Device!.Name);
                break;
            case CheckResult.Disabled:
                log.LogWarning("{Element}: {Device} — desactivat a Windows; assignat igualment", element, check.Device!.Name);
                break;
            case CheckResult.IdChanged:
                log.LogInformation("{Element}: {Device} — ha canviat d'identificador (p. ex. un USB en un altre port); és el mateix dispositiu", element, check.Device!.Name);
                break;
            case CheckResult.Ambiguous:
                log.LogWarning("{Element}: hi ha diversos dispositius «{Device}»; cal triar-ne un. Queda sense dispositiu", element, saved);
                break;
            default:
                log.LogWarning("{Element}: «{Device}» no hi és. Queda sense dispositiu fins que torni o se'n triï un altre", element, saved);
                break;
        }
    }

    foreach (var device in report.NewDevices)
        log.LogInformation("Maquinari: dispositiu nou disponible: {Device}", device.Name);
    foreach (var change in report.FormatChanges)
        log.LogInformation("Maquinari: {Device}: format canviat a Windows ({Before} → {After})",
            change.Device.Name, FormatText(change.PreviousFormat!), FormatText(change.Device.Format!));

    static string FormatText(DeviceFormat f) => $"{f.SampleRate} Hz, {f.Bits} bits, {f.Channels} canals";
}

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
