using System.Text.Json;
using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine.Data;

namespace OnaBages.Engine.Protocol;

/// <summary>
/// Rep el text d'una ordre, la valida i en retorna la resposta.
/// Totes les ordres noves s'afegeixen aquí (i a docs/protocol.md, i a les proves).
/// </summary>
public sealed class CommandRouter
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
                "player.retry" => PlayerCommand(msg, p => p.Retry()),
                "player.seek" => PlayerSeek(msg),
                "player.setVolume" => PlayerSetVolume(msg),
                "player.playItem" => PlayerCommand(msg, p => p.PlayItem(RequireString(msg, "itemId"))),
                "playlist.get" => new JsonObject { ["playlist"] = PlaylistJson.Playlist(GetPlayer(msg)) },
                "playlist.add" => PlaylistAdd(msg),
                "playlist.move" => PlaylistMove(msg),
                "playlist.remove" => PlaylistRemove(msg),
                "playlist.setFlags" => PlaylistSetFlags(msg),
                "playlist.duplicate" => PlaylistDuplicate(msg),
                "playlist.transfer" => PlaylistTransfer(msg),
                "playlist.load" => PlaylistLoad(msg),
                "playlist.save" => PlaylistSave(msg),
                "track.info" => TrackInfoGet(msg),
                "recorder.setDevice" => RecorderSetDevice(msg),
                "recorder.start" => RecorderStart(msg),
                "recorder.stop" => RecorderCommand(msg, r => r.Stop()),
                "recorder.configure" => RecorderConfigure(msg),
                "recorder.setGain" => RecorderSetGain(msg),
                "recording.save" => RecordingSave(msg),
                "recording.discard" => RecordingDiscard(msg),
                "recording.remove" => RecordingRemove(msg),
                "cue.play" => CuePlay(msg),
                "cue.stop" => CueCommand(c => c.Stop()),
                "cue.seek" => CueCommand(c => c.Seek(TimeSpan.FromSeconds(RequireNumber(msg, "position", "la posició en segons")))),
                "cue.setVolume" => CueSetVolume(msg),
                "cue.setDevice" => CueSetDevice(msg),
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
        catch (Exception ex) when (ex is PlayerCommandException or RecorderCommandException or RecordingCommandException)
        {
            return Messages.Error(id, "bad_state", ex.Message);
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
    };

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
    /// Canvia només els ajustos que s'envien. Ara: "ui": { "zoom": 0.7..1.5 }.
    /// </summary>
    private JsonObject SettingsSet(JsonObject msg)
    {
        var config = RequireConfig();
        if (msg["ui"] is not JsonObject ui)
            throw new BadParamsException("Falta el camp 'ui' amb els ajustos a canviar.");

        if (ui["zoom"] is JsonNode zoomNode)
        {
            if (zoomNode is not JsonValue v || !v.TryGetValue(out double zoom)
                || zoom < EngineConfig.MinZoom || zoom > EngineConfig.MaxZoom)
                throw new BadParamsException($"'ui.zoom' ha de ser un número entre {EngineConfig.MinZoom} i {EngineConfig.MaxZoom}.");
            config.Update(c => c.UiZoom = Math.Round(zoom, 2));
        }

        return new JsonObject { ["settings"] = SettingsJson(config) };
    }

    private static JsonObject SettingsJson(ConfigStore config) => new()
    {
        ["ui"] = new JsonObject { ["zoom"] = config.Read(c => c.UiZoom) },
    };

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
            ["missing"] = entries.Count(e => !File.Exists(e.Path)),
        };
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
                entries.AddRange(PlaylistFiles.AudioFilesInFolder(path).Select(f => new PlaylistEntry(f)));
            else if (PlaylistFiles.IsPlaylistFile(path)) entries.AddRange(PlaylistFiles.Read(path));
            else entries.Add(new PlaylistEntry(path));
        }
        return entries;
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

    /// <summary>L'enregistrador que tenia aquest fitxer com a pendent torna a Inactiu.</summary>
    private void ReleasePending(string path)
    {
        foreach (var recorder in _state.Recorders)
            recorder.Release(path);
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
