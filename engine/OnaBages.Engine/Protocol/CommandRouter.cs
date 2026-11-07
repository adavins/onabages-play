using System.Text.Json;
using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine.Data;

namespace OnaBages.Engine.Protocol;

/// <summary>
/// Rep el text d'una ordre, la valida i en retorna la resposta.
/// Totes les ordres noves s'afegeixen aquí (i a docs/protocol.md, i a les proves).
/// </summary>
public sealed partial class CommandRouter
{
    private readonly EngineState _state;
    private readonly Action _requestShutdown;
    private readonly Func<IReadOnlyList<AudioDevice>> _listDevices;
    private readonly HardwareAssignments? _hardware;
    private readonly ConfigStore? _config;
    private readonly RecordingList _recordings;

    /// <param name="state">Estat del motor.</param>
    /// <param name="requestShutdown">Acció que atura el motor (s'executa poc després de respondre).</param>
    /// <param name="listDevices">
    /// Funció que retorna els dispositius d'àudio. Per defecte, els reals de Windows;
    /// les proves hi passen una llista inventada.
    /// </param>
    /// <param name="hardware">Assignacions desades (config.json). Les proves que no en necessiten hi poden passar null.</param>
    /// <param name="config">Configuració (ajustos de la interfície). Igual.</param>
    /// <param name="recordings">Enregistraments acabats (pendents i recents). Per defecte, una llista buida.</param>
    public CommandRouter(
        EngineState state,
        Action requestShutdown,
        Func<IReadOnlyList<AudioDevice>>? listDevices = null,
        HardwareAssignments? hardware = null,
        ConfigStore? config = null,
        RecordingList? recordings = null)
    {
        _state = state;
        _requestShutdown = requestShutdown;
        _listDevices = listDevices ?? DeviceList.GetAll;
        _hardware = hardware;
        _config = config;
        _recordings = recordings ?? new RecordingList();
    }

    public JsonObject Handle(string text)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
            // Els camps es llegeixen la primera vegada que es fan servir: es força ara perquè
            // un camp repetit (p. ex. dos "id") doni bad_json aquí i no un error més endavant.
            if (node is JsonObject obj) _ = obj.Count;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return Messages.Error(null, "bad_json", "El missatge no és JSON vàlid (o té camps repetits).");
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
                "engine.shutdown" => Shutdown(msg),
                "devices.list" => DevicesList(msg),
                "hardware.report" => HardwareReportGet(),
                "settings.get" => SettingsGet(),
                "settings.set" => SettingsSet(msg),
                "player.setDevice" => PlayerSetDevice(msg),
                "player.load" => PlayerLoad(msg),
                "player.play" => PlayerCommand(msg, p => p.Play()),
                "player.pause" => PlayerCommand(msg, p => p.Pause()),
                "player.stop" => PlayerCommand(msg, p => p.Stop()),
                // Fade-out (pas 20; D-107): la durada és la del perfil (settings.fadeOut.ms).
                "player.fadeOut" => PlayerCommand(msg, p => p.FadeOut(_config?.Read(c => c.FadeOutMs) ?? EngineConfig.DefaultFadeOutMs)),
                "player.retry" => PlayerCommand(msg, p => p.Retry()),
                "player.seek" => PlayerSeek(msg),
                "player.setVolume" => PlayerSetVolume(msg),
                "player.setProcessing" => PlayerSetProcessing(msg),
                "player.setNormalization" => PlayerSetNormalization(msg),
                "player.setCrossfade" => PlayerSetCrossfade(msg),
                "player.setTrim" => PlayerSetTrim(msg),
                "processing.savePreset" => ProcessingSavePreset(msg),
                "processing.deletePreset" => ProcessingDeletePreset(msg),
                "player.playItem" => PlayerCommand(msg, p => p.PlayItem(RequireString(msg, "itemId"))),
                "playlist.get" => new JsonObject { ["playlist"] = PlaylistJson.Playlist(GetPlayer(msg)) },
                "playlist.add" => PlaylistAdd(msg),
                "playlist.move" => PlaylistMove(msg),
                "playlist.remove" => PlaylistRemove(msg),
                "playlist.setFlags" => PlaylistSetFlags(msg),
                "playlist.duplicate" => PlaylistDuplicate(msg),
                "playlist.transfer" => PlaylistTransfer(msg),
                "playlist.addSection" => PlaylistAddSection(msg),
                "playlist.updateSection" => PlaylistUpdateSection(msg),
                "playlist.load" => PlaylistLoad(msg),
                "playlist.peek" => PlaylistPeek(msg),
                "playlist.save" => PlaylistSave(msg),
                "track.info" => TrackInfoGet(msg),
                "recorder.setDevice" => RecorderSetDevice(msg),
                "recorder.start" => RecorderStart(msg),
                "recorder.stop" => RecorderCommand(msg, r => r.Stop()),
                "recorder.mark" => RecorderCommand(msg, r => r.Mark()),
                "recorder.nameMark" => RecorderNameMark(msg),
                "recorder.configure" => RecorderConfigure(msg),
                "recorder.setGain" => RecorderSetGain(msg),
                "recorder.setLimiter" => RecorderSetLimiter(msg),
                "recording.save" => RecordingSave(msg),
                "recording.saveEdited" => RecordingSaveEdited(msg),
                "recording.discard" => RecordingDiscard(msg),
                "recording.remove" => RecordingRemove(msg),
                "recordings.clear" => RecordingsClear(msg),
                "cue.play" => CuePlay(msg),
                "cue.stop" => CueCommand(c => c.Stop()),
                "cue.seek" => CueCommand(c => c.Seek(TimeSpan.FromSeconds(RequireNumber(msg, "position", "la posició en segons")))),
                "cue.setVolume" => CueSetVolume(msg),
                "cue.setDevice" => CueSetDevice(msg),
                "editor.get" => EditorState(),
                "editor.open" => EditorOpen(msg),
                "editor.close" => EditorDo(e => e.Close()),
                "editor.delete" => EditorChange(msg, (edit, start, end) => edit.Delete(start, end)),
                "editor.keep" => EditorChange(msg, (edit, start, end) => edit.Keep(start, end)),
                "editor.fadeIn" => EditorChange(msg, (edit, start, end) => edit.FadeIn(start, end)),
                "editor.fadeOut" => EditorChange(msg, (edit, start, end) => edit.FadeOut(start, end)),
                "editor.undo" => EditorDo(e => e.Change(edit => edit.Undo())),
                "editor.redo" => EditorDo(e => e.Change(edit => edit.Redo())),
                "editor.save" => EditorSave(msg),
                "editor.saveSelection" => EditorSaveSelection(msg),
                "editor.moveMark" => EditorMoveMark(msg),
                "editor.peaks" => EditorPeaks(msg),
                "editor.play" => EditorPlay(msg),
                "editor.stop" => EditorDo(e => e.StopPlayback()),
                "editor.setDevice" => EditorSetDevice(msg),
                "profile.list" => ProfileList(),
                "profile.create" => ProfileCreate(msg),
                "profile.rename" => ProfileRename(msg),
                "profile.delete" => ProfileDelete(msg),
                "profile.switch" => ProfileSwitch(msg),
                "profile.export" => ProfileExport(msg),
                "profile.import" => ProfileImport(msg),
                "profile.checkPin" => ProfileCheckPin(msg),
                "profile.setPin" => ProfileSetPin(msg),
                "explorer.get" => ExplorerGet(),
                "explorer.setFolders" => ExplorerSetFolders(msg),
                "explorer.list" => ExplorerList(msg),
                _ => null,
            };

            return result is null
                ? Messages.Error(id, "unknown_command", $"Ordre desconeguda: {cmd}")
                : Messages.Ok(id, result);
        }
        catch (PlaylistFileException ex)
        {
            return Messages.Error(id, "file_error", ex.Message);
        }
        catch (BadParamsException ex)
        {
            return Messages.Error(id, "bad_params", ex.Message);
        }
        catch (Exception ex) when (ex is PlayerCommandException or RecorderCommandException or RecordingCommandException
                                       or EditorStateException or Mp3EditException)
        {
            return Messages.Error(id, "bad_state", ex.Message);
        }
        catch (EditorFileExistsException ex)
        {
            return Messages.Error(id, "file_exists", ex.Message);
        }
        catch (ProfileException ex)
        {
            return Messages.Error(id, ex.Code, ex.Message);
        }
        catch (RecordingActiveException ex)
        {
            return Messages.Error(id, "recording_active", ex.Message);
        }
        catch (RecorderFailedException ex)
        {
            return Messages.Error(id, "recorder_error", ex.Message);
        }
        catch (PlayerFailedException ex)
        {
            return Messages.Error(id, "player_error", ex.Message);
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
        ["players"] = PlayerJson.All(_state.Players),
        ["recorders"] = RecorderJson.All(_state.Recorders),
        ["recordings"] = RecordingJson.All(_recordings),
        ["cue"] = CueJson.Cue(_state),
        ["playlists"] = PlaylistJson.All(_state.Players),
        ["hardware"] = HardwareJson.Report(_hardware?.Report ?? HardwareReport.Empty),
        ["editor"] = EditorJson.Editor(_state.Editor, _config?.Read(c => c.EditorDevice)),
    };

    // -----------------------------------------------------------------------
    // Editor (fase 3; docs/protocol.md, 4.10)
    // -----------------------------------------------------------------------

    private JsonObject EditorState() => new() { ["editor"] = EditorJson.Editor(_state.Editor, _config?.Read(c => c.EditorDevice)) };

    private JsonObject EditorDo(Action<EditorSession> action)
    {
        action(_state.Editor);
        return EditorState();
    }

    /// <summary>Obre un MP3. Mai un fitxer que s'està enregistrant (especificació 9.4).</summary>
    private JsonObject EditorOpen(JsonObject msg)
    {
        string path = RequireString(msg, "path");
        if (!Path.IsPathFullyQualified(path))
            throw new BadParamsException("'path' ha de ser una ruta completa.");
        if (!path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            throw new BadParamsException("L'editor només obre fitxers MP3.");
        RejectIfRecording(path);

        _state.Editor.Open(path);
        return EditorState();
    }

    /// <summary>Elimina o conserva [start, end) (segons de la versió editada; s'ajusten al fotograma més proper).</summary>
    private JsonObject EditorChange(JsonObject msg, Func<Mp3Edit, int, int, bool> change)
    {
        double start = RequireNumber(msg, "start", "el començament en segons");
        double end = RequireNumber(msg, "end", "el final en segons");
        bool changed = _state.Editor.Change(edit => change(edit, edit.FrameAt(start), edit.FrameAt(end)));
        var result = EditorState();
        result["changed"] = changed;
        return result;
    }

    /// <summary>Desa: a <c>path</c> (fitxer nou) o, sense <c>path</c>, a sobre de l'original. Sobreescriure demana <c>overwrite: true</c>.</summary>
    private JsonObject EditorSave(JsonObject msg)
    {
        string? target = ReadString(msg["path"]);
        if (target is not null && !Path.IsPathFullyQualified(target))
            throw new BadParamsException("'path' ha de ser una ruta completa.");
        bool overwrite = msg["overwrite"] is JsonValue v && v.TryGetValue(out bool b) && b;
        if (target is not null) RejectIfRecording(target);

        _state.Editor.Save(target, overwrite);
        return EditorState();
    }

    /// <summary>
    /// Desa només la selecció (fase 5, pas 23d): <c>start</c> i <c>end</c> (segons de la versió editada) i <c>path</c>
    /// (fitxer nou, ruta completa). Sobreescriure demana <c>overwrite: true</c>. L'edició no canvia.
    /// </summary>
    private JsonObject EditorSaveSelection(JsonObject msg)
    {
        double start = RequireNumber(msg, "start", "el començament en segons");
        double end = RequireNumber(msg, "end", "el final en segons");
        string target = RequireString(msg, "path");
        if (!Path.IsPathFullyQualified(target))
            throw new BadParamsException("'path' ha de ser una ruta completa.");
        bool overwrite = msg["overwrite"] is JsonValue v && v.TryGetValue(out bool b) && b;
        RejectIfRecording(target);

        _state.Editor.SaveSelection(start, end, target, overwrite);
        return EditorState();
    }

    /// <summary>Mou una marca (fase 5, pas 23f): <c>number</c> (1, 2...) i <c>seconds</c> (de la versió editada). Es desa de seguida.</summary>
    private JsonObject EditorMoveMark(JsonObject msg)
    {
        int number = (int)RequireNumber(msg, "number", "el número de la marca (1, 2...)");
        double seconds = RequireNumber(msg, "seconds", "la posició en segons");
        _state.Editor.MoveMark(number, seconds);
        return EditorState();
    }

    /// <summary>Pics de la forma d'ona (fotogrames de l'original): opcionals <c>from</c> i <c>count</c>; per defecte, tots els que hi ha.</summary>
    private JsonObject EditorPeaks(JsonObject msg)
    {
        int from = msg["from"] is null ? 0 : (int)RequireNumber(msg, "from", "el primer fotograma (0 o més)");
        int count = msg["count"] is null ? int.MaxValue : (int)Math.Min(int.MaxValue, RequireNumber(msg, "count", "quants fotogrames"));
        if (from < 0 || count < 0)
            throw new BadParamsException("'from' i 'count' han de ser 0 o més.");
        var (start, data) = _state.Editor.ReadPeaks(from, count);
        return new JsonObject { ["peaks"] = EditorJson.Peaks(start, data) };
    }

    /// <summary>Fa sonar la versió editada: opcionals <c>from</c> i <c>to</c> (segons; per defecte, de l'inici al final).</summary>
    private JsonObject EditorPlay(JsonObject msg)
    {
        double from = msg["from"] is null ? 0 : RequireNumber(msg, "from", "la posició en segons");
        double? to = msg["to"] is null ? null : RequireNumber(msg, "to", "la posició en segons");
        string deviceId = EditorOutput();

        _state.Editor.Play(deviceId, from, to);
        if (_state.Editor.Player.State == EditorPlayState.Error)
            throw new PlayerFailedException(_state.Editor.Player.ErrorMessage ?? "Error en reproduir a l'editor.");
        return EditorState();
    }

    /// <summary>
    /// Sortida de l'editor: la triada (per identificador o, si ha canviat, pel nom) o, si no se n'ha triat
    /// cap, la de la preescolta. Mai el dispositiu predeterminat de Windows.
    /// </summary>
    private string EditorOutput()
    {
        if (_config?.Read(c => c.EditorDevice) is not { } saved)
            return _state.Cue.DeviceId
                ?? throw new EditorStateException("Cal triar una sortida per a l'editor (o per a la preescolta).");

        var outputs = _listDevices().Where(d => d.Kind == DeviceKind.Output).ToList();
        var byId = outputs.FirstOrDefault(d => d.Id == saved.Id);
        if (byId is not null) return byId.Id;
        var byName = outputs.Where(d => d.Name == saved.Name).ToList();
        if (byName.Count == 1) return byName[0].Id;
        throw new EditorStateException($"La sortida de l'editor («{saved.Name}») no hi és. Tria'n una altra.");
    }

    /// <summary>Tria la sortida de l'editor. <c>deviceId: null</c> = la de la preescolta (per defecte).</summary>
    private JsonObject EditorSetDevice(JsonObject msg)
    {
        var config = RequireConfig();
        if (!msg.ContainsKey("deviceId"))
            throw new BadParamsException("Falta el camp 'deviceId' (una sortida, o null per fer servir la de la preescolta).");

        DeviceRef? device = null;
        if (ReadString(msg["deviceId"]) is { } id)
        {
            var found = _listDevices().FirstOrDefault(d => d.Id == id && d.Kind == DeviceKind.Output)
                ?? throw new BadParamsException("No hi ha cap sortida amb aquest identificador.");
            device = new DeviceRef(found.Id, found.Name);
        }
        else if (msg["deviceId"] is not null)
        {
            throw new BadParamsException("'deviceId' ha de ser un text o null.");
        }

        _state.Editor.StopPlayback(); // si sonava, per la sortida d'abans
        config.Update(c => c.EditorDevice = device);
        return EditorState();
    }

    private void RejectIfRecording(string path)
    {
        bool recording = _state.Recorders.Any(r => r.State == RecorderState.Recording
            && string.Equals(r.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (recording || File.Exists(path + ".rec"))
            throw new EditorStateException(recording
                ? "Aquest fitxer s'està enregistrant: no es pot obrir a l'editor fins que s'aturi."
                : "Aquest fitxer és un enregistrament interromput (té el marcador .rec): cal recuperar-lo abans d'editar-lo.");
    }

    /// <summary>Resum de l'arrencada: què ha passat amb els dispositius desats (especificació 5.6).</summary>
    private JsonObject HardwareReportGet() => new()
    {
        ["report"] = HardwareJson.Report(_hardware?.Report ?? HardwareReport.Empty),
    };

    // -----------------------------------------------------------------------
    // Ajustos (es desen a config.json)
    // -----------------------------------------------------------------------

    private ConfigStore RequireConfig() =>
        _config ?? throw new InvalidOperationException("Aquest motor no té configuració.");

    private JsonObject SettingsGet() => new() { ["settings"] = SettingsJson(RequireConfig()) };

    /// <summary>
    /// Canvia només els ajustos que s'envien (docs/protocol.md, «settings.set»):
    /// "ui": { zoom, recordersPerRow, endWarningSeconds, hideIdleRecorderMeters, playlistRowHeight, playerOrder, recorderOrder }, "layout": { players, recorders } i "program": { name }.
    /// Primer es comprova tot; si algun valor és fora de marge, no es canvia res.
    /// </summary>
    private JsonObject SettingsSet(JsonObject msg)
    {
        var config = RequireConfig();
        var ui = msg["ui"] as JsonObject;
        var layout = msg["layout"] as JsonObject;
        var program = msg["program"] as JsonObject;
        var loudness = msg["loudness"] as JsonObject;
        var trim = msg["trim"] as JsonObject;
        var limiter = msg["recorderLimiter"] as JsonObject;
        var archive = msg["recordingArchive"] as JsonObject;
        var recents = msg["recents"] as JsonObject;
        var fadeOut = msg["fadeOut"] as JsonObject;
        if (ui is null && layout is null && program is null && loudness is null && trim is null && limiter is null && archive is null && recents is null && fadeOut is null)
            throw new BadParamsException("Falta el camp 'ui', 'layout', 'program', 'loudness', 'trim', 'fadeOut', 'recorderLimiter', 'recordingArchive' o 'recents' amb els ajustos a canviar.");
        int? fadeOutMs = OptionalInt(fadeOut?["ms"], "fadeOut.ms", Player.MinFadeOutMs, Player.MaxFadeOutMs);
        bool? clearOnExit = null;
        if (recents?["clearOnExit"] is { } clearNode)
            clearOnExit = clearNode is JsonValue cv && cv.TryGetValue(out bool cb) ? cb : throw new BadParamsException("'recents.clearOnExit' ha de ser true o false.");

        double? zoom = OptionalNumber(ui?["zoom"], "ui.zoom", EngineConfig.MinZoom, EngineConfig.MaxZoom);
        int? perRow = OptionalInt(ui?["recordersPerRow"], "ui.recordersPerRow", 1, EngineConfig.MaxRecorders);
        int? warning = OptionalInt(ui?["endWarningSeconds"], "ui.endWarningSeconds", 0, EngineConfig.MaxEndWarningSeconds);
        bool? hideIdleMeters = null;
        if (ui?["hideIdleRecorderMeters"] is { } hideNode)
            hideIdleMeters = hideNode is JsonValue hv && hv.TryGetValue(out bool hb) ? hb : throw new BadParamsException("'ui.hideIdleRecorderMeters' ha de ser true o false.");
        int? rowHeight = OptionalInt(ui?["playlistRowHeight"], "ui.playlistRowHeight", EngineConfig.MinPlaylistRowHeight, EngineConfig.MaxPlaylistRowHeight);
        List<string>? playerOrder = OptionalOrder(ui?["playerOrder"], "ui.playerOrder", EngineConfig.MaxPlayers);
        List<string>? recorderOrder = OptionalOrder(ui?["recorderOrder"], "ui.recorderOrder", EngineConfig.MaxRecorders);
        int? players = OptionalInt(layout?["players"], "layout.players", 1, EngineConfig.MaxPlayers);
        int? recorders = OptionalInt(layout?["recorders"], "layout.recorders", 1, EngineConfig.MaxRecorders);
        string? programName = OptionalProgramName(program?["name"]);
        double? target = OptionalNumber(loudness?["targetLufs"], "loudness.targetLufs", EngineConfig.MinLoudnessTargetLufs, EngineConfig.MaxLoudnessTargetLufs);
        int? trimThreshold = OptionalInt(trim?["thresholdDb"], "trim.thresholdDb", -80, -40);
        if (trimThreshold is { } tt && !TrackLoudness.SilenceThresholdsDb.Contains(tt))
            throw new BadParamsException("'trim.thresholdDb' ha de ser -40, -50, -60, -70 o -80.");
        int? trimLead = OptionalInt(trim?["leadMs"], "trim.leadMs", 0, Player.MaxTrimMarginMs);
        int? trimTail = OptionalInt(trim?["tailMs"], "trim.tailMs", 0, Player.MaxTrimMarginMs);
        double? ceiling = OptionalNumber(limiter?["ceilingDb"], "recorderLimiter.ceilingDb", Recorder.MinLimiterCeilingDb, Recorder.MaxLimiterCeilingDb);
        bool? archiveEnabled = null;
        if (archive?["enabled"] is { } enabledNode)
            archiveEnabled = enabledNode is JsonValue ev && ev.TryGetValue(out bool b) ? b : throw new BadParamsException("'recordingArchive.enabled' ha de ser true o false.");
        // Carpeta: "" = la de per defecte; si no, una ruta completa.
        string? archiveFolder = archive?["folder"] is { } folderNode
            ? ReadString(folderNode) ?? throw new BadParamsException("'recordingArchive.folder' ha de ser un text.")
            : null;
        if (archiveFolder is { Length: > 0 } && !Path.IsPathFullyQualified(archiveFolder))
            throw new BadParamsException("'recordingArchive.folder' ha de ser una ruta completa (p. ex. E:\\Historial).");
        int? keepDays = OptionalInt(archive?["keepDays"], "recordingArchive.keepDays", 0, RecordingArchive.MaxKeepDays);

        config.Update(c =>
        {
            if (zoom is { } z) c.UiZoom = Math.Round(z, 2);
            if (perRow is { } r) c.RecordersPerRow = r;
            if (warning is { } w) c.EndWarningSeconds = w;
            if (hideIdleMeters is { } hm) c.HideIdleRecorderMeters = hm;
            if (rowHeight is { } rh) c.PlaylistRowHeight = rh;
            if (playerOrder is not null) c.PlayerOrder = playerOrder;
            if (recorderOrder is not null) c.RecorderOrder = recorderOrder;
            // El nombre d'elements s'aplica en tornar a arrencar el motor. Les assignacions i els ajustos
            // dels que es treuen no s'esborren: si es tornen a posar, tornen tal com eren.
            if (players is { } p) c.PlayerCount = p;
            if (recorders is { } n) c.RecorderCount = n;
            if (programName is not null) c.ProgramName = programName;
            if (target is { } t) c.LoudnessTargetLufs = Math.Round(t, 1);
            if (trimThreshold is { } th) c.TrimThresholdDb = th;
            if (trimLead is { } lead) c.TrimLeadMs = lead;
            if (trimTail is { } tail) c.TrimTailMs = tail;
            if (fadeOutMs is { } fo) c.FadeOutMs = fo; // s'aplica al pròxim fade-out
            if (ceiling is { } ce) c.RecorderLimiterCeilingDb = Math.Round(ce, 1);
            if (archiveEnabled is { } ae) c.RecordingArchiveEnabled = ae;
            if (archiveFolder is not null) c.RecordingArchiveFolder = archiveFolder.Length == 0 ? null : archiveFolder;
            if (keepDays is { } kd) c.RecordingArchiveKeepDays = kd;
            if (clearOnExit is { } co) c.ClearRecentsOnExit = co;
        });

        // L'historial s'aplica a partir del pròxim enregistrament (el que està en curs continua com era).
        // Els dies antics s'esborren en arrencar el motor.
        if (archive is not null) ApplyRecordingArchive(config);

        // El sostre nou s'aplica de seguida a tots els enregistradors (també als que enregistren).
        if (ceiling is not null)
        {
            var (limited, newCeiling) = config.Read(c => (c.LimitedRecorders.ToHashSet(), c.RecorderLimiterCeilingDb));
            foreach (var recorder in _state.Recorders)
                recorder.SetLimiter(limited.Contains(recorder.Id), newCeiling);
        }

        // El retall nou s'aplica a tots els reproductors (a partir de la pista següent).
        if (trim is not null)
        {
            var (trimmed, th, lead, tail) = config.Read(c => (c.TrimPlayers.ToHashSet(), c.TrimThresholdDb, c.TrimLeadMs, c.TrimTailMs));
            foreach (var player in _state.Players)
                player.SetTrim(new Player.TrimSettings(trimmed.Contains(player.Id), th, lead, tail));
        }

        // L'objectiu nou s'aplica a tots els reproductors (a partir de la pista següent).
        if (target is not null)
        {
            var (normalized, newTarget) = config.Read(c => (c.NormalizedPlayers.ToHashSet(), c.LoudnessTargetLufs));
            foreach (var player in _state.Players)
                player.SetNormalization(normalized.Contains(player.Id), newTarget);
        }

        return new JsonObject { ["settings"] = SettingsJson(config) };
    }

    /// <summary>
    /// Carpeta de l'historial (la del perfil actiu, D-088) a tots els enregistradors: després de canviar els ajustos
    /// o el nom del perfil. S'aplica a partir del pròxim enregistrament.
    /// </summary>
    private void ApplyRecordingArchive(ConfigStore config)
    {
        string? folder = config.Read(c => c.ProfileRecordingArchiveFolder);
        foreach (var recorder in _state.Recorders)
            recorder.SetArchive(folder);
    }

    private JsonObject SettingsJson(ConfigStore config)
    {
        var (zoom, perRow, warning, players, recorders, programName) =
            config.Read(c => (c.UiZoom, c.RecordersPerRow, c.EndWarningSeconds, c.PlayerCount, c.RecorderCount, c.ProgramName));
        bool hideIdleMeters = config.Read(c => c.HideIdleRecorderMeters);
        int rowHeight = config.Read(c => c.PlaylistRowHeight);
        // Còpia dins del bloqueig: la llista de la configuració no surt d'aquí.
        var (playerOrder, recorderOrder) = config.Read(c => (OrderJson(c.PlayerOrder), OrderJson(c.RecorderOrder)));
        var (profileId, profileName) = config.Read(c => (c.ProfileId, c.ProfileName));
        return new JsonObject
        {
            // Perfil actiu (fase 4): la configuració de treball d'aquest programa.
            ["profile"] = new JsonObject { ["id"] = profileId, ["name"] = profileName },
            // Normalització de sonoritat (fase 4, pas 5b).
            ["loudness"] = new JsonObject { ["targetLufs"] = config.Read(c => c.LoudnessTargetLufs), ["maxBoostDb"] = Player.MaxNormalizationBoostDb },
            // Retall de silencis (fase 5): comú a tots els reproductors del perfil; cada un l'activa amb player.setTrim.
            ["trim"] = config.Read(c => new JsonObject
            {
                ["thresholdDb"] = c.TrimThresholdDb,
                ["leadMs"] = c.TrimLeadMs,
                ["tailMs"] = c.TrimTailMs,
                ["thresholds"] = new JsonArray(TrackLoudness.SilenceThresholdsDb.Select(t => (JsonNode)t).ToArray()),
                ["maxMarginMs"] = Player.MaxTrimMarginMs,
            }),
            // Fade-out dels reproductors (fase 5, pas 20; del perfil).
            ["fadeOut"] = new JsonObject
            {
                ["ms"] = config.Read(c => c.FadeOutMs),
                ["minMs"] = Player.MinFadeOutMs,
                ["maxMs"] = Player.MaxFadeOutMs,
            },
            // Enregistraments recents (fase 5, pas 11a; de l'ordinador).
            ["recents"] = new JsonObject { ["clearOnExit"] = config.Read(c => c.ClearRecentsOnExit) },
            // Historial d'enregistraments (fase 5, pas 10a; de l'ordinador): còpia de cada original.
            ["recordingArchive"] = config.Read(c => new JsonObject
            {
                ["enabled"] = c.RecordingArchiveEnabled,
                ["folder"] = c.RecordingArchiveFolder ?? RecordingArchive.DefaultFolder,
                ["defaultFolder"] = RecordingArchive.DefaultFolder,
                ["keepDays"] = c.RecordingArchiveKeepDays,
                ["maxKeepDays"] = RecordingArchive.MaxKeepDays,
            }),
            // Limitador dels enregistradors (fase 5, pas 9a): sostre comú; cada un l'activa amb recorder.setLimiter.
            ["recorderLimiter"] = new JsonObject
            {
                ["ceilingDb"] = config.Read(c => c.RecorderLimiterCeilingDb),
                ["minCeilingDb"] = Recorder.MinLimiterCeilingDb,
                ["maxCeilingDb"] = Recorder.MaxLimiterCeilingDb,
            },
            // Presets del compressor/limitador dels reproductors (fase 4, pas 4b).
            ["processingPresets"] = ProcessingPresetsJson(),
            ["ui"] = new JsonObject { ["zoom"] = zoom, ["recordersPerRow"] = perRow, ["endWarningSeconds"] = warning, ["hideIdleRecorderMeters"] = hideIdleMeters,
                ["playlistRowHeight"] = rowHeight, ["minPlaylistRowHeight"] = EngineConfig.MinPlaylistRowHeight, ["maxPlaylistRowHeight"] = EngineConfig.MaxPlaylistRowHeight,
                ["playerOrder"] = playerOrder, ["recorderOrder"] = recorderOrder },
            ["layout"] = new JsonObject { ["players"] = players, ["recorders"] = recorders },
            ["program"] = new JsonObject { ["name"] = programName },
            // Carpetes que la interfície pot obrir (botons de ⚙ Configuració).
            ["folders"] = new JsonObject { ["history"] = PlayHistoryFile.DefaultFolder, ["logs"] = EngineLogFile.DefaultFolder },
            // El nombre desat no és el que té ara el motor: s'aplicarà en tornar a obrir l'aplicació.
            ["restartNeeded"] = players != _state.Players.Count || recorders != _state.Recorders.Count,
        };
    }

    /// <summary>
    /// Ordre a la pantalla (pas 16a): llista d'identificadors («1», «2»…) sense repetir-ne cap. Si no s'envia, null;
    /// una llista buida torna a l'ordre per número.
    /// </summary>
    private static List<string>? OptionalOrder(JsonNode? node, string name, int max)
    {
        if (node is null) return null;
        if (node is not JsonArray array || array.Any(i => ReadString(i) is null))
            throw new BadParamsException($"'{name}' ha de ser una llista d'identificadors (p. ex. [\"2\", \"1\"]).");
        var ids = array.Select(i => ReadString(i)!).ToList();
        if (!EngineConfig.IsValidOrder(ids, max))
            throw new BadParamsException($"'{name}' només pot tenir identificadors de 1 a {max}, sense repetir-ne cap.");
        return ids;
    }

    private static JsonArray OrderJson(List<string> ids) => new(ids.Select(id => (JsonNode)id).ToArray());

    /// <summary>Nom del programa opcional (si no s'envia, null; "" el treu).</summary>
    private static string? OptionalProgramName(JsonNode? node)
    {
        if (node is null) return null;
        if (node is not JsonValue v || !v.TryGetValue(out string? name))
            throw new BadParamsException("'program.name' ha de ser un text.");
        string clean = ConfigRepository.CleanProgramName(name, cut: false);
        if (clean.Length > EngineConfig.MaxProgramName)
            throw new BadParamsException($"El nom del programa pot tenir com a molt {EngineConfig.MaxProgramName} caràcters.");
        return clean;
    }

    /// <summary>Número opcional dins d'un marge (si no s'envia, null).</summary>
    private static double? OptionalNumber(JsonNode? node, string field, double min, double max)
    {
        if (node is null) return null;
        if (node is not JsonValue v || !v.TryGetValue(out double value) || value < min || value > max)
            throw new BadParamsException($"'{field}' ha de ser un número entre {min} i {max}.");
        return value;
    }

    /// <summary>Enter opcional dins d'un marge (si no s'envia, null).</summary>
    private static int? OptionalInt(JsonNode? node, string field, int min, int max)
    {
        if (node is null) return null;
        if (node is not JsonValue v || !v.TryGetValue(out double value) || value != Math.Floor(value) || value < min || value > max)
            throw new BadParamsException($"'{field}' ha de ser un nombre enter entre {min} i {max}.");
        return (int)value;
    }

    // -----------------------------------------------------------------------
    // Reproductors
    // -----------------------------------------------------------------------

    private Player GetPlayer(JsonObject msg)
    {
        string? id = ReadString(msg["player"]);
        if (string.IsNullOrWhiteSpace(id))
            throw new BadParamsException("Falta el camp 'player' (identificador del reproductor).");
        return _state.FindPlayer(id) ?? throw new BadParamsException($"No existeix el reproductor '{id}'.");
    }

    private static string RequireString(JsonObject msg, string field)
    {
        string? value = ReadString(msg[field]);
        if (string.IsNullOrWhiteSpace(value))
            throw new BadParamsException($"Falta el camp '{field}'.");
        return value;
    }

    private JsonObject PlayerSetDevice(JsonObject msg)
    {
        var player = GetPlayer(msg);
        string deviceId = RequireString(msg, "deviceId");
        player.SetDevice(deviceId);
        // Es desa encara que la sortida falli en obrir-se: és la que ha triat l'operador.
        _hardware?.Remember(ElementKind.Player, player.Id, deviceId, _listDevices());
        return PlayerResult(player);
    }

    private JsonObject PlayerLoad(JsonObject msg)
    {
        var player = GetPlayer(msg);
        player.Load(RequireString(msg, "path"));
        return PlayerResult(player);
    }

    /// <summary>Paràmetre "position": segons des de l'inici de la pista.</summary>
    private JsonObject PlayerSeek(JsonObject msg)
    {
        var player = GetPlayer(msg);
        if (msg["position"] is not JsonValue v || !v.TryGetValue(out double seconds) || double.IsNaN(seconds) || seconds < 0)
            throw new BadParamsException("'position' ha de ser un nombre de segons (0 o més).");

        player.Seek(TimeSpan.FromSeconds(seconds));
        return PlayerResult(player);
    }

    // -----------------------------------------------------------------------
    // Llistes (docs/protocol.md, 4.5)
    // -----------------------------------------------------------------------

    /// <summary>"paths": fitxers (rutes completes); "beforeId" (opcional): on s'insereixen (per defecte, al final).</summary>
    private JsonObject PlaylistAdd(JsonObject msg)
    {
        var player = GetPlayer(msg);
        var paths = RequireStrings(msg, "paths");
        if (paths.Any(p => !Path.IsPathFullyQualified(p)))
            throw new BadParamsException("Cada element de 'paths' ha de ser una ruta completa.");

        // Si hi ha llistes (.obpl, .m3u) o carpetes, s'hi afegeixen les seves pistes (D-035).
        var ids = player.Playlist.AddEntries(ExpandPlaylists(paths), ExistingItem(player, ReadString(msg["beforeId"])));
        return new JsonObject { ["ids"] = new JsonArray(ids.Select(id => (JsonNode)id).ToArray()) };
    }

    /// <summary>"ids": pistes a moure (en l'ordre de la llista); "beforeId" (opcional): davant de quina (per defecte, al final).</summary>
    private JsonObject PlaylistMove(JsonObject msg)
    {
        var player = GetPlayer(msg);
        var ids = RequireStrings(msg, "ids");
        foreach (var id in ids) ExistingItem(player, id);
        player.Playlist.Move(ids, ExistingItem(player, ReadString(msg["beforeId"])));
        return new JsonObject { ["playlist"] = PlaylistJson.Playlist(player) };
    }

    private JsonObject PlaylistRemove(JsonObject msg)
    {
        var player = GetPlayer(msg);
        var ids = RequireStrings(msg, "ids");
        player.Playlist.Remove(ids);
        return new JsonObject { ["playlist"] = PlaylistJson.Playlist(player) };
    }

    /// <summary>"itemId" i, opcionals, "loop", "autoNext", "cue" (bool). Els que no s'envien no canvien.</summary>
    private JsonObject PlaylistSetFlags(JsonObject msg)
    {
        var player = GetPlayer(msg);
        string id = ExistingItem(player, RequireString(msg, "itemId"))!;
        player.Playlist.SetFlags(id, ReadBool(msg["loop"]), ReadBool(msg["autoNext"]), ReadBool(msg["cue"]));
        return new JsonObject { ["playlist"] = PlaylistJson.Playlist(player) };
    }

    /// <summary>
    /// Obre una llista desada (.obpl) o un M3U: "path"; "replace" (per defecte true) substitueix
    /// la llista actual; si és false, s'afegeix al final. La pista que sona continua sonant.
    /// </summary>
    private JsonObject PlaylistLoad(JsonObject msg)
    {
        var player = GetPlayer(msg);
        string path = RequireString(msg, "path");
        if (!Path.IsPathFullyQualified(path))
            throw new BadParamsException("'path' ha de ser una ruta completa.");

        var entries = PlaylistFiles.Read(path);
        bool replace = ReadBool(msg["replace"]) ?? true;
        if (replace)
            player.Playlist.Remove(player.Playlist.Items.Select(i => i.Id).ToList());
        var ids = player.Playlist.AddEntries(entries);

        return new JsonObject
        {
            ["ids"] = new JsonArray(ids.Select(id => (JsonNode)id).ToArray()),
            ["missing"] = entries.Count(e => !e.IsSection && !File.Exists(e.Path)),
        };
    }

    /// <summary>
    /// Contingut d'una llista desada, sense carregar-la enlloc (pestanya Llistes del panell lateral, pas 2d):
    /// "path" (ruta completa d'un .obpl, .m3u o .m3u8). { items: [{ section } | { path, exists }], tracks, missing }
    /// </summary>
    private static JsonObject PlaylistPeek(JsonObject msg)
    {
        string path = RequireString(msg, "path");
        if (!Path.IsPathFullyQualified(path))
            throw new BadParamsException("'path' ha de ser una ruta completa.");

        var entries = PlaylistFiles.Read(path);
        var items = new JsonArray();
        int tracks = 0, missing = 0;
        foreach (var entry in entries)
        {
            if (entry.IsSection)
            {
                items.Add(new JsonObject { ["section"] = entry.SectionTitle, ["color"] = entry.Color });
                continue;
            }
            bool exists = File.Exists(entry.Path);
            tracks++;
            if (!exists) missing++;
            items.Add(new JsonObject { ["path"] = entry.Path, ["exists"] = exists });
        }
        return new JsonObject { ["items"] = items, ["tracks"] = tracks, ["missing"] = missing };
    }

    /// <summary>Desa la llista d'un reproductor en format .obpl: "path" (ruta completa; s'hi afegeix .obpl si no en té).</summary>
    private JsonObject PlaylistSave(JsonObject msg)
    {
        var player = GetPlayer(msg);
        string path = RequireString(msg, "path");
        if (!Path.IsPathFullyQualified(path))
            throw new BadParamsException("'path' ha de ser una ruta completa.");
        if (!path.EndsWith(PlaylistFiles.Extension, StringComparison.OrdinalIgnoreCase))
            path += PlaylistFiles.Extension;

        PlaylistFiles.Write(path, player.Playlist.Items);
        return new JsonObject { ["path"] = path, ["count"] = player.Playlist.Items.Count };
    }

    /// <summary>
    /// Fitxers d'àudio tal qual; les llistes (.obpl, .m3u), substituïdes per les seves pistes;
    /// les carpetes, pels seus fitxers d'àudio (també els de les subcarpetes).
    /// </summary>
    private static List<PlaylistEntry> ExpandPlaylists(IEnumerable<string> paths)
    {
        var entries = new List<PlaylistEntry>();
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
                entries.AddRange(PlaylistFiles.AudioFilesInFolder(path).Select(f => PlaylistEntry.ForFile(f)));
            else if (PlaylistFiles.IsPlaylistFile(path)) entries.AddRange(PlaylistFiles.Read(path));
            else entries.Add(PlaylistEntry.ForFile(path)); // «______.mp3»: separador de secció
        }
        return entries;
    }

    /// <summary>
    /// Separador de secció (pas 9c): "title"; opcionals "color" (un de Playlist.SectionColors) i
    /// "beforeId" (on s'insereix; per defecte, al final).
    /// </summary>
    private JsonObject PlaylistAddSection(JsonObject msg)
    {
        var player = GetPlayer(msg);
        string title = RequireSectionTitle(msg);
        string? color = ReadSectionColor(msg) is { Length: > 0 } c ? c : null;
        string id = player.Playlist.AddSection(title, color, ExistingItem(player, ReadString(msg["beforeId"])));
        return new JsonObject { ["id"] = id };
    }

    /// <summary>
    /// Canvia un separador: "itemId" i, opcionals, "title", "color" (null o "" el treu) i "collapsed" (bool).
    /// Els que no s'envien no canvien.
    /// </summary>
    private JsonObject PlaylistUpdateSection(JsonObject msg)
    {
        var player = GetPlayer(msg);
        string id = ExistingItem(player, RequireString(msg, "itemId"))!;
        string? title = msg.ContainsKey("title") ? RequireSectionTitle(msg) : null;
        player.Playlist.UpdateSection(id, title, ReadSectionColor(msg), ReadBool(msg["collapsed"]));
        return new JsonObject { ["playlist"] = PlaylistJson.Playlist(player) };
    }

    private static string RequireSectionTitle(JsonObject msg)
    {
        string? title = ReadString(msg["title"])?.Trim();
        if (string.IsNullOrEmpty(title))
            throw new BadParamsException("Falta el camp 'title' (el nom de la secció).");
        if (title.Length > Playlist.MaxSectionTitle)
            throw new BadParamsException($"El nom de la secció pot tenir com a molt {Playlist.MaxSectionTitle} caràcters.");
        return title;
    }

    /// <summary>"color": null si no s'envia; "" si s'envia null o buit (treure'l); si no, un dels permesos.</summary>
    private static string? ReadSectionColor(JsonObject msg)
    {
        if (!msg.ContainsKey("color")) return null;
        string? color = ReadString(msg["color"]);
        if (string.IsNullOrEmpty(color)) return "";
        if (!Playlist.SectionColors.Contains(color))
            throw new BadParamsException($"'color' ha de ser un d'aquests: {string.Join(", ", Playlist.SectionColors)} (o null).");
        return color;
    }

    /// <summary>"ids": cada pista es duplica just a sota de l'original.</summary>
    private JsonObject PlaylistDuplicate(JsonObject msg)
    {
        var player = GetPlayer(msg);
        var ids = RequireStrings(msg, "ids");
        foreach (var id in ids) ExistingItem(player, id);
        var added = player.Playlist.Duplicate(ids);
        return new JsonObject { ["ids"] = new JsonArray(added.Select(id => (JsonNode)id).ToArray()) };
    }

    /// <summary>
    /// Arrossegar pistes a una llista (la mateixa o una altra): "ids" (de la llista de "player"),
    /// "toPlayer", "beforeId" opcional (de la llista de destí) i "copy" (per defecte false: es mouen).
    /// Es conserven les marques de bucle i d'avanç automàtic.
    /// </summary>
    private JsonObject PlaylistTransfer(JsonObject msg)
    {
        var from = GetPlayer(msg);
        string? toId = ReadString(msg["toPlayer"]);
        var to = _state.FindPlayer(toId) ?? throw new BadParamsException($"No existeix el reproductor '{toId}'.");
        var ids = RequireStrings(msg, "ids");
        var items = ids.Select(id => from.Playlist.Find(ExistingItem(from, id))!).ToList();
        string? beforeId = ExistingItem(to, ReadString(msg["beforeId"]));
        bool copy = ReadBool(msg["copy"]) ?? false;

        if (!copy && ReferenceEquals(from, to))
        {
            from.Playlist.Move(ids, beforeId); // dins la mateixa llista, moure és canviar l'ordre
            return new JsonObject { ["ids"] = new JsonArray(ids.Select(id => (JsonNode)id).ToArray()) };
        }

        var added = to.Playlist.AddCopies(items, beforeId);
        if (!copy) from.Playlist.Remove(ids);
        return new JsonObject { ["ids"] = new JsonArray(added.Select(id => (JsonNode)id).ToArray()) };
    }

    /// <summary>Metadades d'un fitxer, per a la finestra d'informació. Paràmetre "path".</summary>
    private static JsonObject TrackInfoGet(JsonObject msg)
    {
        string path = RequireString(msg, "path");
        try
        {
            return new JsonObject { ["info"] = PlaylistJson.Metadata(TrackInfo.ReadMetadata(path)) };
        }
        catch (AudioFileException ex)
        {
            throw new BadParamsException(ex.Message);
        }
    }

    /// <summary>L'identificador ha de ser d'una pista de la llista (o null). Si no, bad_params.</summary>
    private static string? ExistingItem(Player player, string? id)
    {
        if (id is not null && player.Playlist.Find(id) is null)
            throw new BadParamsException($"La pista '{id}' no és a la llista del reproductor {player.Id}.");
        return id;
    }

    private static List<string> RequireStrings(JsonObject msg, string field)
    {
        if (msg[field] is not JsonArray array || array.Count == 0)
            throw new BadParamsException($"Falta el camp '{field}' (una llista).");
        var list = new List<string>();
        foreach (var node in array)
        {
            string? value = ReadString(node);
            if (string.IsNullOrWhiteSpace(value))
                throw new BadParamsException($"'{field}' només pot tenir textos.");
            list.Add(value);
        }
        return list;
    }

    private static bool? ReadBool(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out bool b) ? b : null;

    /// <summary>Paràmetre "db": volum de −60 (silenci) a 0. Fora de marge, es queda al límit.</summary>
    private JsonObject PlayerSetVolume(JsonObject msg)
    {
        var player = GetPlayer(msg);
        if (msg["db"] is not JsonValue v || !v.TryGetValue(out double db) || double.IsNaN(db))
            throw new BadParamsException("'db' ha de ser un nombre (de −60 a 0).");

        player.SetVolume(db);
        // El volum es pot canviar també amb el reproductor en error: es respon sempre amb l'estat.
        return new JsonObject { ["player"] = PlayerJson.Player(player) };
    }

    private JsonObject PlayerCommand(JsonObject msg, Action<Player> action)
    {
        var player = GetPlayer(msg);
        action(player);
        return PlayerResult(player);
    }

    /// <summary>
    /// Resposta d'una ordre de reproductor. Si l'ordre l'ha deixat en Error
    /// (dispositiu no disponible, fitxer il·legible), la resposta és un error amb el motiu.
    /// </summary>
    private static JsonObject PlayerResult(Player player)
    {
        if (player.State == PlayerState.Error)
            throw new PlayerFailedException(player.ErrorMessage ?? "Error del reproductor.");
        return new JsonObject { ["player"] = PlayerJson.Player(player) };
    }

    /// <summary>L'ordre ha deixat el reproductor en estat d'error: es respon amb "player_error".</summary>
    private sealed class PlayerFailedException(string message) : Exception(message);

    // -----------------------------------------------------------------------
    // Enregistradors
    // -----------------------------------------------------------------------

    private Recorder GetRecorder(JsonObject msg)
    {
        string? id = ReadString(msg["recorder"]);
        if (string.IsNullOrWhiteSpace(id))
            throw new BadParamsException("Falta el camp 'recorder' (identificador de l'enregistrador).");
        return _state.FindRecorder(id) ?? throw new BadParamsException($"No existeix l'enregistrador '{id}'.");
    }

    private JsonObject RecorderSetDevice(JsonObject msg)
    {
        var recorder = GetRecorder(msg);
        string deviceId = RequireString(msg, "deviceId");
        recorder.SetDevice(deviceId);
        _hardware?.Remember(ElementKind.Recorder, recorder.Id, deviceId, _listDevices());
        // Canviar l'entrada sempre funciona, encara que l'enregistrador tingui un error anterior.
        return new JsonObject { ["recorder"] = RecorderJson.Recorder(recorder) };
    }

    /// <summary>Paràmetres opcionals: "folder" (carpeta de treball) i "name" (per al nom del fitxer).</summary>
    private JsonObject RecorderStart(JsonObject msg)
    {
        var recorder = GetRecorder(msg);
        string? folder = ReadString(msg["folder"]);
        string? name = ReadString(msg["name"]);
        if (folder is not null && !Path.IsPathFullyQualified(folder))
            throw new BadParamsException("'folder' ha de ser una ruta completa (p. ex. D:\\Enregistraments).");

        recorder.Start(string.IsNullOrWhiteSpace(folder) ? null : folder, string.IsNullOrWhiteSpace(name) ? null : name);
        return RecorderResult(recorder);
    }

    /// <summary>Opcionals "name" (buit = el de per defecte) i "folder" (ruta completa). S'apliquen al pròxim enregistrament.</summary>
    private JsonObject RecorderConfigure(JsonObject msg)
    {
        var recorder = GetRecorder(msg);
        string? name = ReadString(msg["name"]);
        string? folder = ReadString(msg["folder"]);
        if (name is null && folder is null)
            throw new BadParamsException("Cal enviar 'name' o 'folder'.");
        if (folder is not null && !Path.IsPathFullyQualified(folder))
            throw new BadParamsException("'folder' ha de ser una ruta completa (p. ex. D:\\Enregistraments).");

        recorder.Configure(name, folder);
        SaveRecorderSettings(recorder);
        return new JsonObject { ["recorder"] = RecorderJson.Recorder(recorder) };
    }

    /// <summary>Paràmetre "db": volum d'enregistrament de −60 (silenci) a +12. Fora de marge, es queda al límit.</summary>
    private JsonObject RecorderSetGain(JsonObject msg)
    {
        var recorder = GetRecorder(msg);
        if (msg["db"] is not JsonValue v || !v.TryGetValue(out double db) || double.IsNaN(db))
            throw new BadParamsException("'db' ha de ser un nombre (de −60 a +12).");

        recorder.SetGain(db);
        SaveRecorderSettings(recorder);
        return new JsonObject { ["recorder"] = RecorderJson.Recorder(recorder) };
    }

    /// <summary>
    /// Limitador d'un enregistrador (fase 5, pas 9a; D-083): recorder, enabled. S'aplica de seguida, també a mig
    /// enregistrament. El sostre és comú (settings.set recorderLimiter). Es desa al perfil.
    /// </summary>
    private JsonObject RecorderSetLimiter(JsonObject msg)
    {
        var recorder = GetRecorder(msg);
        if (msg["enabled"] is not JsonValue v || !v.TryGetValue(out bool enabled))
            throw new BadParamsException("'enabled' ha de ser true o false.");

        double ceiling = _config?.Read(c => c.RecorderLimiterCeilingDb) ?? Recorder.DefaultLimiterCeilingDb;
        recorder.SetLimiter(enabled, ceiling);
        _config?.Update(c =>
        {
            if (enabled) c.LimitedRecorders.Add(recorder.Id);
            else c.LimitedRecorders.Remove(recorder.Id);
        });
        return new JsonObject { ["recorder"] = RecorderJson.Recorder(recorder) };
    }

    /// <summary>El nom, la carpeta i el volum de cada enregistrador es desen a config.json.</summary>
    private void SaveRecorderSettings(Recorder recorder) =>
        _config?.Update(c => c.RecorderSettings[recorder.Id] = new RecorderSettings(
            recorder.Name == recorder.DefaultName ? null : recorder.Name,
            recorder.Folder == Recorder.DefaultFolder ? null : recorder.Folder,
            recorder.GainDb));

    // -----------------------------------------------------------------------
    // Preescolta (especificació 7)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Opcionals "path" (ruta completa; sense, torna a fer sonar l'última) i "position" (segons on comença).
    /// Si ja sonava una altra pista, la substitueix.
    /// </summary>
    private JsonObject CuePlay(JsonObject msg)
    {
        string? path = ReadString(msg["path"]);
        if (path is not null && !Path.IsPathFullyQualified(path))
            throw new BadParamsException("'path' ha de ser una ruta completa.");
        double position = msg["position"] is null ? 0 : RequireNumber(msg, "position", "la posició en segons");

        _state.Cue.Play(string.IsNullOrWhiteSpace(path) ? null : path, TimeSpan.FromSeconds(Math.Max(0, position)));
        return CueResult();
    }

    /// <summary>"db": volum de la preescolta, de −60 (silenci) a 0. Es desa a config.json.</summary>
    private JsonObject CueSetVolume(JsonObject msg)
    {
        _state.Cue.SetVolume(RequireNumber(msg, "db", "un nombre (de −60 a 0)"));
        _config?.Update(c => c.CueVolumeDb = _state.Cue.VolumeDb);
        return new JsonObject { ["cue"] = CueJson.Cue(_state) };
    }

    /// <summary>"deviceId": sortida de la preescolta. Si sonava, s'atura. Es desa a config.json.</summary>
    private JsonObject CueSetDevice(JsonObject msg)
    {
        string deviceId = RequireString(msg, "deviceId");
        _state.Cue.SetDevice(deviceId);
        _hardware?.Remember(ElementKind.Cue, EngineState.CueId, deviceId, _listDevices());
        return new JsonObject { ["cue"] = CueJson.Cue(_state) };
    }

    private JsonObject CueCommand(Action<CuePlayer> action)
    {
        action(_state.Cue);
        return CueResult();
    }

    /// <summary>Si l'ordre ha deixat la preescolta en Error, la resposta és un error amb el motiu ("player_error").</summary>
    private JsonObject CueResult()
    {
        if (_state.Cue.State == CueState.Error)
            throw new PlayerFailedException(_state.Cue.ErrorMessage ?? "Error de la preescolta.");
        return new JsonObject { ["cue"] = CueJson.Cue(_state) };
    }

    /// <summary>Un camp numèric obligatori.</summary>
    private static double RequireNumber(JsonObject msg, string field, string description)
    {
        if (msg[field] is not JsonValue v || !v.TryGetValue(out double value) || double.IsNaN(value) || double.IsInfinity(value))
            throw new BadParamsException($"'{field}' ha de ser {description}.");
        return value;
    }

    // -----------------------------------------------------------------------
    // Enregistraments acabats: pendents i recents (D-036)
    // -----------------------------------------------------------------------

    private RecordingItem GetRecording(JsonObject msg)
    {
        string id = RequireString(msg, "recordingId");
        return _recordings.Find(id) ?? throw new BadParamsException($"No hi ha cap enregistrament '{id}' a la llista.");
    }

    /// <summary>"recordingId" i "path" (ruta completa on desar-lo). Si era el pendent d'un enregistrador, aquest torna a Inactiu.</summary>
    private JsonObject RecordingSave(JsonObject msg)
    {
        var item = GetRecording(msg);
        string path = RequireString(msg, "path");
        if (!Path.IsPathFullyQualified(path))
            throw new BadParamsException("'path' ha de ser una ruta completa.");

        string target = _recordings.Save(item.Id, path);
        ReleasePending(item.Path);
        return new JsonObject { ["path"] = target, ["recording"] = RecordingJson.Item(_recordings.Find(item.Id) ?? item) };
    }

    /// <summary>
    /// "recordingId" i "path": el fitxer que l'editor acaba de desar amb la versió editada (fase 5, pas 14b; D-102).
    /// L'enregistrament passa a ser aquest fitxer i queda desat; si era el pendent d'un enregistrador, aquest torna a
    /// Inactiu. L'original va a la paperera si n'hi ha còpia a l'historial; si no, es queda on és.
    /// </summary>
    private JsonObject RecordingSaveEdited(JsonObject msg)
    {
        var item = GetRecording(msg);
        string path = RequireString(msg, "path");
        if (!Path.IsPathFullyQualified(path))
            throw new BadParamsException("'path' ha de ser una ruta completa.");

        // Durada de la versió editada, llegida del fitxer (només les capçaleres dels fotogrames: és ràpid).
        // Si no es pot llegir, es manté la de l'original (només es veu a Recents).
        TimeSpan? duration = File.Exists(path) ? RecordingList.DurationOf(path) : null;

        var (updated, recycled, kept) = _recordings.SaveEdited(item.Id, path, duration);
        ReleasePending(item.Path);
        return new JsonObject
        {
            ["recording"] = RecordingJson.Item(updated),
            ["original"] = new JsonObject { ["path"] = item.Path, ["recycled"] = recycled, ["kept"] = kept },
        };
    }

    /// <summary>"recordingId": envia el fitxer a la paperera i el treu de la llista.</summary>
    private JsonObject RecordingDiscard(JsonObject msg)
    {
        var item = GetRecording(msg);
        _recordings.Discard(item.Id);
        ReleasePending(item.Path);
        return new JsonObject { ["discarded"] = item.Id };
    }

    /// <summary>"recordingId": el treu de la llista de recents (el fitxer no es toca).</summary>
    private JsonObject RecordingRemove(JsonObject msg)
    {
        var item = GetRecording(msg);
        _recordings.Remove(item.Id);
        return new JsonObject { ["removed"] = item.Id };
    }

    /// <summary>
    /// Neteja Recents (fase 5, pas 11a; D-089): treu els desats i els no trobats; amb "includeUnsaved": true, també els
    /// no desats. Els fitxers no es toquen; el pendent de cada enregistrador i els que s'estan desant es queden.
    /// </summary>
    private JsonObject RecordingsClear(JsonObject msg)
    {
        bool includeUnsaved = false;
        if (msg["includeUnsaved"] is { } node)
            includeUnsaved = node is JsonValue v && v.TryGetValue(out bool b) ? b : throw new BadParamsException("'includeUnsaved' ha de ser true o false.");
        var (removed, unsavedLeft) = _recordings.Clear(includeUnsaved);
        return new JsonObject { ["removed"] = removed, ["unsavedLeft"] = unsavedLeft };
    }

    /// <summary>L'enregistrador que tenia aquest fitxer com a pendent torna a Inactiu.</summary>
    private void ReleasePending(string path)
    {
        foreach (var recorder in _state.Recorders)
            recorder.Release(path);
    }

    /// <summary>Nom d'una marca (fase 5, pas 23e): recorder, number (1, 2...) i name ("" o null = sense nom).</summary>
    private JsonObject RecorderNameMark(JsonObject msg)
    {
        int number = (int)RequireNumber(msg, "number", "el número de la marca (1, 2...)");
        string? name = msg["name"] is null ? null
            : ReadString(msg["name"]) ?? throw new BadParamsException("'name' ha de ser un text.");
        return RecorderCommand(msg, r => r.NameMark(number, name));
    }

    private JsonObject RecorderCommand(JsonObject msg, Action<Recorder> action)
    {
        var recorder = GetRecorder(msg);
        action(recorder);
        return RecorderResult(recorder);
    }

    /// <summary>Si l'ordre ha deixat l'enregistrador en Error, la resposta és un error amb el motiu.</summary>
    private static JsonObject RecorderResult(Recorder recorder)
    {
        if (recorder.State == RecorderState.Error)
            throw new RecorderFailedException(recorder.ErrorMessage ?? "Error de l'enregistrador.");
        return new JsonObject { ["recorder"] = RecorderJson.Recorder(recorder) };
    }

    private sealed class RecorderFailedException(string message) : Exception(message);

    private sealed class RecordingActiveException(string message) : Exception(message);

    /// <summary>
    /// Atura el motor. Si hi ha enregistraments en curs es rebutja, tret que es demani
    /// amb "force": true (llavors els fitxers es tanquen correctament abans d'aturar-se).
    /// </summary>
    private JsonObject Shutdown(JsonObject msg)
    {
        bool force = msg["force"] is JsonValue v && v.TryGetValue(out bool b) && b;
        if (_state.IsRecording && !force)
            throw new RecordingActiveException(
                "Hi ha enregistraments en curs. Atura'ls primer (o envia \"force\": true per tancar-los i aturar el motor).");

        _ = Task.Run(async () =>
        {
            await Task.Delay(200); // deixa temps perquè la resposta arribi al client
            _requestShutdown();
        });

        return new JsonObject { ["stopping"] = true };
    }

    /// <summary>
    /// Llista de dispositius d'àudio.
    /// Paràmetres opcionals: "kind" ("output", "input" o "all") i "includeInactive" (bool).
    /// </summary>
    private JsonObject DevicesList(JsonObject msg)
    {
        string kind = ReadString(msg["kind"]) ?? "all";
        if (kind is not ("all" or "output" or "input"))
            throw new BadParamsException("'kind' ha de ser \"output\", \"input\" o \"all\".");

        bool includeInactive = msg["includeInactive"] is JsonValue v && v.TryGetValue(out bool b) && b;

        var list = new JsonArray();
        foreach (var device in _listDevices())
        {
            if (kind == "output" && device.Kind != DeviceKind.Output) continue;
            if (kind == "input" && device.Kind != DeviceKind.Input) continue;
            if (!includeInactive && device.Status != DeviceStatus.Active) continue;

            list.Add(DeviceJson.Device(device));
        }

        return new JsonObject { ["devices"] = list };
    }

    /// <summary>Error en els paràmetres d'una ordre: es respon amb el codi "bad_params".</summary>
    private sealed class BadParamsException(string message) : Exception(message);

    private static int? ReadInt(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out int i) ? i : null;

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? s) ? s : null;
}
