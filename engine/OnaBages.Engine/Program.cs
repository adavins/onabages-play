using System.Net;
using System.Text;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

// Punt d'entrada del motor d'Ona Bages Play.
// Ús: OnaBages.Engine [--port 47800] [--token <testimoni>] [--data-dir <carpeta>]

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

// Mode portable (D-044): totes les dades a la carpeta que diu la interfície. Abans de llegir res.
if (options.DataDir is { } dataDir)
    RuntimeFile.UseDataDirectory(dataDir);

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

// Registre tècnic també en fitxer (un per dia a %APPDATA%\OnaBages\logs\, es guarden 30 dies).
using var logFile = new EngineLogFile();
builder.Logging.AddProvider(new EngineLogFileProvider(logFile));

var app = builder.Build();
var log = app.Logger;
log.LogInformation("{Name} {Version} arrencant (port {Port}, PID {Pid}, dades a {DataDir})", EngineInfo.Name, EngineInfo.Version, options.Port, Environment.ProcessId, RuntimeFile.DirectoryPath);

// Un error no previst que fes caure el motor: com a mínim, que quedi al registre.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    log.LogCritical("Error no previst: el motor es tanca. {Error}", e.ExceptionObject);
    logFile.Flush(TimeSpan.FromSeconds(2));
};
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    log.LogError("Error no previst en una tasca de fons (el motor continua): {Error}", e.Exception);
    e.SetObserved();
};

// Formes d'ona de l'editor que fa temps que no es fan servir (memòria cau).
PeakCacheFile.DeleteOld();

// La carpeta de l'historial existeix des del principi (la interfície la pot obrir encara que estigui buida).
try { Directory.CreateDirectory(PlayHistoryFile.DefaultFolder); }
catch (Exception ex) { log.LogWarning("No s'ha pogut crear la carpeta de l'historial: {Message}", ex.Message); }

// Comprova en segon pla les pistes que s'afegeixen a les llistes (una per a tot el motor).
// També en mesura la sonoritat (fase 4, pas 5b), amb memòria cau (les mesures de fa més de 60 dies s'esborren).
var loudnessCache = new LoudnessCacheFile();
loudnessCache.DeleteOld();
var analyzer = new TrackAnalyzer(cache: loudnessCache);

// Quants reproductors i enregistradors hi ha: ho diu la configuració (pas 9 de la fase 2; per defecte, 2 i 2).
// Es llegeix només el nombre; la resta de la configuració la llegeix més avall el ConfigStore.
// La primera vegada amb perfils, aquesta lectura converteix la configuració antiga en el primer perfil (D-057).
var configRepository = new ConfigRepository();
var savedLayout = configRepository.Load().Config;
int playerCount = savedLayout.PlayerCount;
int recorderCount = savedLayout.RecorderCount;
// Les últimes llistes són del perfil actiu. El perfil només canvia en reiniciar el motor.
string lastListsFolder = configRepository.LastListsFolder(savedLayout.ProfileId);
log.LogInformation("Perfil: {Name} ({Folder})", savedLayout.ProfileName, configRepository.ProfileFolder(savedLayout.ProfileId));
var state = new EngineState(
    Enumerable.Range(1, playerCount).Select(i => new Player(i.ToString(), analyzer: analyzer)),
    Enumerable.Range(1, recorderCount).Select(i => new Recorder(i.ToString())),
    new CuePlayer(analyzer: analyzer));
log.LogInformation("Distribució: {Players} reproductors i {Recorders} enregistradors", playerCount, recorderCount);
var hub = new ClientHub(state, log);

// Llistes que han canviat: s'envien agrupades a la revisió següent (com a molt 10 per segon),
// encara que se n'hagin afegit cent pistes de cop.
var playlistsChanged = new System.Collections.Concurrent.ConcurrentDictionary<Player, bool>();
// L'última llista de cada reproductor es desa sola (com a molt cada 2 s) i es recupera en arrencar (D-035).
var playlistsToSave = new System.Collections.Concurrent.ConcurrentDictionary<Player, bool>();
foreach (var player in state.Players)
{
    player.Playlist.Changed += () => playlistsChanged[player] = true;
    player.Playlist.Changed += () => playlistsToSave[player] = true;
}

void SaveLastList(Player player)
{
    try { PlaylistFiles.Write(PlaylistFiles.LastListPath(lastListsFolder, player.Id), player.Playlist.Items); }
    catch (PlaylistFileException ex) { log.LogWarning("Reproductor {Id}: no s'ha pogut desar la llista: {Message}", player.Id, ex.Message); }
}

foreach (var player in state.Players)
{
    string lastList = PlaylistFiles.LastListPath(lastListsFolder, player.Id);
    if (!File.Exists(lastList)) continue;
    try
    {
        var entries = PlaylistFiles.Read(lastList);
        player.Playlist.AddEntries(entries);
        log.LogInformation("Reproductor {Id}: recuperada la llista ({Count} pistes)", player.Id, entries.Count);
    }
    catch (PlaylistFileException ex)
    {
        log.LogWarning("Reproductor {Id}: no s'ha pogut recuperar la llista: {Message}", player.Id, ex.Message);
    }
}

// Envia un esdeveniment a tots els clients sense esperar (no pot bloquejar qui el crida).
void Broadcast(System.Text.Json.Nodes.JsonObject ev) => _ = Task.Run(async () =>
{
    try { await hub.BroadcastAsync(ev, app.Lifetime.ApplicationStopping); }
    catch (OperationCanceledException) { /* el motor s'està aturant */ }
});

// Durada dels àudios de l'explorador (fase 5, pas 15a): a grups, a mesura que el fil de prioritat baixa les mesura.
state.Durations.Measured += batch =>
{
    var durations = new System.Text.Json.Nodes.JsonObject();
    foreach (var (path, seconds) in batch) durations[path] = seconds;
    var ev = Messages.Event("explorer.durations");
    ev["durations"] = durations;
    Broadcast(ev);
};

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
    string lastLogged = "";
    recorder.StateChanged += r =>
    {
        // Al registre només hi va el canvi d'estat, de fitxer o d'error (no cada moviment del fader).
        string summary = $"{r.State}|{r.FilePath}|{r.ErrorMessage}";
        if (summary != lastLogged)
        {
            lastLogged = summary;
            if (r.State == RecorderState.Error)
                log.LogWarning("Enregistrador {Id}: error — {Message} (detall: {Detail})", r.Id, r.ErrorMessage, r.ErrorDetail ?? "cap");
            else
                log.LogInformation("Enregistrador {Id}: {State} {File}", r.Id, RecorderJson.State(r.State), r.FilePath ?? "");
        }
        Broadcast(RecorderJson.StateEvent(r));
    };
}

// Botonera (fase 5, pas 28a): cada canvi va a la interfície; al registre, els errors de la sortida.
{
    string? lastError = null;
    state.Cart.StateChanged += cart =>
    {
        if (cart.ErrorMessage != lastError)
        {
            lastError = cart.ErrorMessage;
            if (lastError is not null) log.LogWarning("Botonera: {Message}", lastError);
            else log.LogInformation("Botonera: sortida a punt");
        }
        Broadcast(CartJson.StateEvent(state));
    };
}

// Preescolta: cada canvi va a la interfície; al registre, només el canvi d'estat, de pista o d'error.
{
    string lastLogged = "";
    state.Cue.StateChanged += cue =>
    {
        string summary = $"{cue.State}|{cue.FilePath}|{cue.ErrorMessage}";
        if (summary != lastLogged)
        {
            lastLogged = summary;
            if (cue.State == CueState.Error)
                log.LogWarning("Preescolta: error — {Message} (detall: {Detail})", cue.ErrorMessage, cue.ErrorDetail ?? "cap");
            else
                log.LogInformation("Preescolta: {State} {File}", CueJson.State(cue.State), Path.GetFileName(cue.FilePath) ?? "");
        }
        Broadcast(CueJson.StateEvent(state));
    };
}

// Enregistraments acabats: el pendent de cada enregistrador i els recents (D-036).
// La llista es desa (com a molt cada 2 s) i es recupera en arrencar.
var recordings = new RecordingList(isPending: path => state.Recorders.Any(r =>
    r.State == RecorderState.Pending && string.Equals(r.FilePath, path, StringComparison.OrdinalIgnoreCase)));
recordings.AddLoaded(RecentRecordingsFile.Read());
// 1 = ha canviat: cal enviar-la a la interfície (a la revisió de 100 ms) i desar-la (a la de 2 s).
int recordingsToSend = 0, recordingsToSave = 0;
recordings.Changed += () =>
{
    Interlocked.Exchange(ref recordingsToSend, 1);
    Interlocked.Exchange(ref recordingsToSave, 1);
};
foreach (var recorder in state.Recorders)
    recorder.Finished += finished => recordings.Add(finished);

void SaveRecordings()
{
    try { RecentRecordingsFile.Write(recordings.Items); }
    catch (Exception ex) { log.LogWarning("No s'ha pogut desar la llista d'enregistraments recents: {Message}", ex.Message); }
}

// Configuració desada (config.json): dispositiu de cada element i ajustos de la interfície.
var config = new ConfigStore(configRepository);
config.Warning += message => log.LogWarning("Configuració: {Message}", message);
var hardware = new HardwareAssignments(state, config);

hardware.Warning += message => log.LogWarning("Maquinari: {Message}", message);

// Editor (fase 3): cada canvi va a la interfície; al registre, obrir, desar, reproduir i els errors.
{
    string lastLogged = "";
    state.Editor.Changed += () =>
    {
        var editor = state.Editor;
        var player = editor.Player;
        string summary = $"{editor.Path}|{editor.Busy}|{editor.Error}|{editor.SavedPath}|{editor.PeaksError}|{player.State}|{player.ErrorMessage}";
        if (summary != lastLogged)
        {
            lastLogged = summary;
            if (editor.Error is not null)
                log.LogWarning("Editor: {Error}", editor.Error);
            else if (editor.PeaksError is not null)
                log.LogWarning("Editor: {Error}", editor.PeaksError);
            else if (player.State == EditorPlayState.Error)
                log.LogWarning("Editor: error de reproducció — {Message} (detall: {Detail})", player.ErrorMessage, player.ErrorDetail ?? "cap");
            else if (editor.Busy is not null)
                log.LogInformation("Editor: {Busy} {Path}", editor.Busy == "opening" ? "obrint" : "desant", editor.Path);
            else if (editor.SavedPath is not null)
                log.LogInformation("Editor: desat {Path}", editor.SavedPath);
        }
        Broadcast(EditorJson.ChangedEvent(editor, config.Read(c => c.EditorDevice)));
    };
}

// Historial de reproducció (especificació 6.6): què ha sonat per antena, en un CSV per dia.
// Les pistes acabades es posen a la cua i s'escriuen cada 2 s (fora de la revisió de 100 ms).
// Si el fitxer no es pot escriure (p. ex. obert amb l'Excel), es queden a la cua i es torna a provar.
var history = new PlayHistory();
var historyQueue = new System.Collections.Concurrent.ConcurrentQueue<PlayedTrack>();
history.Finished += track => historyQueue.Enqueue(track);
bool historyFailing = false;

void WriteHistory()
{
    // Un sol escriptor alhora (la revisió de 2 s i l'aturada del motor poden coincidir).
    lock (historyQueue)
        WriteHistoryLocked();
}

void WriteHistoryLocked()
{
    while (historyQueue.TryPeek(out var track))
    {
        var (title, artist) = TrackInfo.ReadTags(track.FilePath);
        try
        {
            PlayHistoryFile.Append(track, title, artist);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!historyFailing)
                log.LogWarning("Historial: no s'ha pogut escriure ({Message}). Es tornarà a provar cada 2 s", ex.Message);
            historyFailing = true;
            return;
        }
        if (historyFailing)
            log.LogInformation("Historial: ja es pot tornar a escriure");
        historyFailing = false;
        historyQueue.TryDequeue(out _);
    }
}

var router = new CommandRouter(state, () => app.Lifetime.StopApplication(), hardware: hardware, config: config, recordings: recordings);

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

// Botonera (fase 5, pas 28a): els botons del perfil. Ha d'anar DESPRÉS de hardware.Start, que és qui llegeix la configuració
// (abans, la llista era buida: D-131). La sortida ja és oberta, i l'inici de cada botó es llegeix en segon pla a la seva freqüència.
var cartButtons = config.Read(c => c.CartButtons.ToList());
foreach (var b in cartButtons)
{
    try
    {
        state.Cart.Assign(b.Page, b.Index, b.Path, b.Name);
        state.Cart.Configure(b.Page, b.Index, color: b.Color, gainDb: b.GainDb);
    }
    catch (CartCommandException ex)
    {
        log.LogWarning("Botonera: no s'ha pogut carregar el botó {Page}/{Index}: {Message}", b.Page + 1, b.Index + 1, ex.Message);
    }
}
if (cartButtons.Count > 0)
    log.LogInformation("Botonera: {Count} botons", cartButtons.Count);

// Emissor d'streaming (fase 5, pas 31c): cada canvi va a la interfície; al registre, el canvi d'estat, d'error o de connexió.
{
    string lastLogged = "";
    state.Streamer.StateChanged += s =>
    {
        string summary = $"{s.State}|{s.ErrorMessage}|{s.ConnectionError}|{s.TitleError}";
        if (summary != lastLogged)
        {
            lastLogged = summary;
            if (s.State == StreamerState.Error)
                log.LogWarning("Streaming: error — {Message} (detall: {Detail})", s.ErrorMessage, s.ErrorDetail ?? "cap");
            else if (s.ConnectionError is not null)
                log.LogWarning("Streaming: {State} — {Message}", StreamJson.State(s.State), s.ConnectionError);
            else
                log.LogInformation("Streaming: {State} {Server}", StreamJson.State(s.State), s.Server is { } srv ? $"{srv.Host}:{srv.SourcePort}" : "");
            if (s.TitleError is not null)
                log.LogWarning("Streaming: {Message}", s.TitleError);
        }
        var (servers, serverId) = config.Read(c => (c.StreamServers.ToList(), c.Stream.ServerId));
        Broadcast(StreamJson.StateEvent(s, servers, serverId));
    };
}


// Emissor d'streaming (fase 5, pas 31c): servidor, qualitat, títol i processament del perfil (la font ja l'ha posada hardware.Start).
try { CommandRouter.ApplyStreamSettings(config, state.Streamer); }
catch (Exception ex) { log.LogWarning("Streaming: ajustos desats no vàlids: {Message}", ex.Message); }

// «Netejar Recents en tancar l'aplicació» (fase 5, pas 11a): es fa en arrencar (amb la configuració ja llegida), abans que la interfície vegi la
// llista (així també es fa si l'ordinador va caure). Només els desats i els no trobats: els no desats es queden.
if (config.Read(c => c.ClearRecentsOnExit))
{
    var (cleared, _) = recordings.Clear(includeUnsaved: false);
    if (cleared > 0) log.LogInformation("Recents: s'han tret {Count} enregistraments desats o no trobats.", cleared);
}

// Nom, carpeta i volum de cada enregistrador (config.json; ja s'ha llegit en començar el maquinari).
foreach (var recorder in state.Recorders)
{
    if (config.Read(c => c.RecorderSettings.GetValueOrDefault(recorder.Id)) is not { } settings) continue;
    try
    {
        recorder.Configure(settings.Name ?? "", settings.Folder);
        recorder.SetGain(settings.GainDb);
    }
    catch (RecorderCommandException ex)
    {
        log.LogWarning("Enregistrador {Id}: ajustos desats no vàlids: {Message}", recorder.Id, ex.Message);
    }
}

// Historial d'enregistraments (fase 5, pas 10a): una còpia de cada original. Els dies antics s'esborren en segon pla.
// Les còpies van a la subcarpeta del perfil actiu (D-088); la neteja revisa les de tots els perfils.
var (archiveFolder, profileArchiveFolder, archiveKeepDays) =
    config.Read(c => (c.EffectiveRecordingArchiveFolder, c.ProfileRecordingArchiveFolder, c.RecordingArchiveKeepDays));
foreach (var recorder in state.Recorders)
    recorder.SetArchive(profileArchiveFolder);
// La carpeta existeix des del principi (la interfície la pot obrir encara que estigui buida).
if (profileArchiveFolder is not null)
{
    try { Directory.CreateDirectory(profileArchiveFolder); }
    catch (Exception ex) { log.LogWarning("No s'ha pogut crear la carpeta de l'historial d'enregistraments: {Message}", ex.Message); }
}
if (archiveFolder is not null && archiveKeepDays > 0)
{
    _ = Task.Run(() =>
    {
        int deleted = RecordingArchive.DeleteOld(archiveFolder, archiveKeepDays, DateTime.Today);
        if (deleted > 0)
            log.LogInformation("Historial d'enregistraments: s'han esborrat {Count} còpies de fa més de {Days} dies.", deleted, archiveKeepDays);
    });
}

// Limitador de cada enregistrador (fase 5, pas 9a): el que hi ha al perfil.
foreach (var recorder in state.Recorders)
    recorder.SetLimiter(config.Read(c => c.LimitedRecorders.Contains(recorder.Id)), config.Read(c => c.RecorderLimiterCeilingDb));

// Alarma de silenci dels enregistradors (fase 5, pas 25a): la del perfil.
CommandRouter.ApplySilenceAlarm(config, state.Recorders);

// Volum de la preescolta (config.json; depèn dels auriculars, per això es recorda).
state.Cue.SetVolume(config.Read(c => c.CueVolumeDb));
// Compressor/limitador de cada reproductor (fase 4, pas 4b): el que hi ha al perfil.
foreach (var player in state.Players)
{
    if (config.Read(c => c.PlayerDynamics.GetValueOrDefault(player.Id)) is { } dynamics)
        player.SetDynamics(dynamics);
    // Normalització de sonoritat (fase 4, pas 5b): quins reproductors i amb quin objectiu.
    var (normalize, target) = config.Read(c => (c.NormalizedPlayers.Contains(player.Id), c.LoudnessTargetLufs));
    player.SetNormalization(normalize, target);
    // Anivellador (fase 5, pas 30b): el del perfil.
    var (level, levelerMax) = config.Read(c => (c.LeveledPlayers.Contains(player.Id), c.LevelerMaxBoostDb));
    player.SetLeveler(level, levelerMax);
    // Fos entre pistes encadenades (fase 5).
    player.SetCrossfade(config.Read(c => c.PlayerCrossfadeMs.GetValueOrDefault(player.Id)));
    // Retall de silencis (fase 5).
    player.SetTrim(config.Read(c => new Player.TrimSettings(c.TrimPlayers.Contains(player.Id), c.TrimThresholdDb, c.TrimLeadMs, c.TrimTailMs)));
}

// Enregistraments interromputs (el motor o l'ordinador van caure): es recuperen abans de res
// i surten a «Recents» amb l'etiqueta «Recuperat» (especificació 8.5).
foreach (var result in StartupRecovery.Run(StartupRecovery.FoldersToScan(state.Recorders, recordings,
    config.Read(c => c.RecorderSettings.Values.Select(s => s.Folder).ToList())), recordings))
{
    if (result.Outcome == RecoveryOutcome.Recovered)
        log.LogWarning("Enregistrament interromput recuperat: {Path} ({Duration:hh\\:mm\\:ss}) — {Message}", result.Path, result.Duration, result.Message);
    else
        log.LogWarning("Enregistrament interromput {Path}: {Message}", result.Path, result.Message);
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
    state.Editor.Dispose(); // si s'estava desant, s'atura (el fitxer de destinació queda intacte)
    // Primer els enregistradors: tanquen els fitxers correctament (i esborren el marcador .rec).
    foreach (var recorder in state.Recorders)
    {
        if (recorder.State == RecorderState.Recording)
            log.LogInformation("Enregistrador {Id}: tancant {File}", recorder.Id, recorder.FilePath);
        recorder.Dispose();
    }
    SaveRecordings(); // inclou els que s'acaben de tancar
    lock (history)
        history.FinishAll(DateTime.Now); // el que sonava també va a l'historial
    WriteHistory();
    foreach (var player in state.Players)
        player.Dispose();
    state.Cue.Dispose();
    state.Cart.Dispose();
    if (state.IsStreaming) log.LogInformation("Streaming: aturant l'emissió");
    state.Streamer.Dispose();
    foreach (var player in state.Players)
        SaveLastList(player); // l'última versió de cada llista, per recuperar-la en tornar a arrencar
    analyzer.Dispose();
    RuntimeFile.DeleteIfOwned();
    log.LogInformation("Motor aturat");
});

// Cada 2 s, desa les llistes que han canviat (per recuperar-les si el motor es tanca o cau).
_ = Task.Run(async () =>
{
    var stopping = app.Lifetime.ApplicationStopping;
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
    try
    {
        while (await timer.WaitForNextTickAsync(stopping))
        {
            foreach (var player in playlistsToSave.Keys)
            {
                if (playlistsToSave.TryRemove(player, out _))
                    SaveLastList(player);
            }
            if (Interlocked.Exchange(ref recordingsToSave, 0) == 1)
                SaveRecordings();
            WriteHistory();
        }
    }
    catch (OperationCanceledException)
    {
        // El motor s'està aturant (en aturar-se, les llistes es desen igualment).
    }
});

// Cada 30 s, comprova que els fitxers de les llistes encara existeixen (només si hi són, sense obrir-los).
_ = Task.Run(async () =>
{
    var stopping = app.Lifetime.ApplicationStopping;
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
    try
    {
        while (await timer.WaitForNextTickAsync(stopping))
        {
            foreach (var player in state.Players)
            {
                try { player.Playlist.CheckFiles(); }
                catch (Exception ex) { log.LogWarning("Reproductor {Id}: no s'ha pogut revisar la llista: {Message}", player.Id, ex.Message); }
            }
        }
    }
    catch (OperationCanceledException)
    {
        // El motor s'està aturant.
    }
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
            await hub.BroadcastAsync(MetersJson.Event(state.Players, state.Recorders, state.Cue, state.Cart, state.Streamer), stopping);
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
    var lastSilenceAlarm = new Dictionary<Recorder, bool>();
    int lastStreamReconnects = 0;
    long lastStreamDropped = 0;

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

                // Alarma de silenci (pas 25a): queda al registre quan salta i quan torna el so.
                bool silenceAlarm = recorder.SilenceAlarm;
                if (silenceAlarm != lastSilenceAlarm.GetValueOrDefault(recorder))
                {
                    if (silenceAlarm)
                        log.LogWarning("Enregistrador {Id}: alarma de silenci, fa {Seconds} s que no entra so (a {Elapsed} de l'enregistrament)",
                            recorder.Id, (int)recorder.SilentSeconds, recorder.Elapsed.ToString(@"hh\:mm\:ss"));
                    else
                        log.LogInformation("Enregistrador {Id}: s'ha acabat l'alarma de silenci", recorder.Id);
                }
                lastSilenceAlarm[recorder] = silenceAlarm;
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

            // Historial: què sona ara a cada reproductor.
            string programName = config.Read(c => c.ProgramName);
            lock (history)
            {
                foreach (var player in state.Players)
                    history.Observe(player.Id, player.State, player.CurrentItemId, player.FilePath, player.Duration, DateTime.Now, programName);
            }

            if (state.Players.Any(p => p.State == PlayerState.Playing))
                await hub.BroadcastAsync(PlayerJson.PositionEvent(state.Players), stopping);

            try { state.Cue.Tick(); }
            catch (Exception ex) { log.LogError("Preescolta: error en revisar-la: {Message}", ex.Message); }
            if (state.Cue.State == CueState.Playing)
                await hub.BroadcastAsync(CueJson.PositionEvent(state.Cue), stopping);

            // Botonera (pas 28a): allibera els talls acabats i vigila la sortida; posició 10 per segon mentre en sona algun.
            try { state.Cart.Tick(); }
            catch (Exception ex) { log.LogError("Botonera: error en revisar-la: {Message}", ex.Message); }
            if (state.Cart.Playing.Count > 0)
                await hub.BroadcastAsync(CartJson.PositionEvent(state.Cart), stopping);

            // Streaming (pas 31c): connexió, reconnexions i font; progrés 1 per segon mentre s'emet.
            try { state.Streamer.Tick(); }
            catch (Exception ex) { log.LogError("Streaming: error en revisar-lo: {Message}", ex.Message); }
            if (state.IsStreaming)
            {
                int reconnects = state.Streamer.Reconnects;
                if (reconnects > lastStreamReconnects)
                    log.LogWarning("Streaming: s'ha tornat a connectar ({Count} vegades des de l'inici)", reconnects);
                lastStreamReconnects = reconnects;
                long dropped = state.Streamer.BytesDropped;
                if (dropped > lastStreamDropped)
                    log.LogWarning("Streaming: no s'han pogut enviar {Bytes} bytes (la xarxa no dona l'abast o la connexió s'ha tallat)", dropped - lastStreamDropped);
                lastStreamDropped = dropped;
                if (tick % 10 == 0)
                    await hub.BroadcastAsync(StreamJson.ProgressEvent(state.Streamer), stopping);
            }
            else
            {
                lastStreamReconnects = 0;
                lastStreamDropped = 0;
            }

            // Reproducció de l'editor: final i errors; posició 10 per segon mentre sona.
            try { state.Editor.Player.Tick(); }
            catch (Exception ex) { log.LogError("Editor: error en revisar la reproducció: {Message}", ex.Message); }
            if (state.Editor.Player.State == EditorPlayState.Playing)
                await hub.BroadcastAsync(EditorJson.PositionEvent(state.Editor), stopping);

            foreach (var player in playlistsChanged.Keys)
            {
                if (playlistsChanged.TryRemove(player, out _))
                    await hub.BroadcastAsync(PlaylistJson.ChangedEvent(player), stopping);
            }

            if (Interlocked.Exchange(ref recordingsToSend, 0) == 1)
                await hub.BroadcastAsync(RecordingJson.ChangedEvent(recordings), stopping);
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
