using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine.Data;

namespace OnaBages.Engine.Protocol;

/// <summary>
/// Ordres de l'emissor d'streaming (fase 5, pas 31c; D-143; protocol 4.16). Hi ha un sol emissor. Els servidors, la font
/// i tots els ajustos són del perfil i es desen a cada canvi.
/// </summary>
public sealed partial class CommandRouter
{
    private JsonObject StreamResult() => new() { ["stream"] = StreamObject() };

    private JsonObject StreamObject()
    {
        var (servers, serverId) = _config?.Read(c => (c.StreamServers.ToList(), c.Stream.ServerId)) ?? ([], null);
        return StreamJson.Stream(_state.Streamer, servers, serverId);
    }

    /// <summary>"deviceId": l'entrada que s'emet. Només amb l'emissió aturada.</summary>
    private JsonObject StreamSetDevice(JsonObject msg)
    {
        string deviceId = RequireString(msg, "deviceId");
        if (!_listDevices().Any(d => d.Id == deviceId && d.Kind == DeviceKind.Input))
            throw new BadParamsException("No hi ha cap entrada amb aquest identificador.");
        _state.Streamer.SetDevice(deviceId);
        _hardware?.Remember(ElementKind.Streamer, EngineState.StreamerId, deviceId, _listDevices());
        return StreamResult();
    }

    /// <summary>
    /// "server": { id? (sense, és nou), name, type ("shoutcast" o "icecast"), host, port, password? (sense, es conserva
    /// la que hi havia), mount?, user?, streamId? }. Un servidor nou queda triat si encara no n'hi havia cap.
    /// No es pot canviar el servidor triat mentre s'emet.
    /// </summary>
    private JsonObject StreamSaveServer(JsonObject msg)
    {
        var config = RequireConfig();
        if (msg["server"] is not JsonObject s)
            throw new BadParamsException("Falta 'server' (les dades del servidor).");

        string? id = ReadString(s["id"]);
        var type = ConfigRepository.ParseStreamServerType(ReadString(s["type"]))
            ?? throw new BadParamsException("'type' ha de ser \"shoutcast\" o \"icecast\".");
        string host = CleanStreamHost(ReadString(s["host"]));
        int port = OptionalInt(s["port"], "port", 1, 65535) ?? throw new BadParamsException("Falta 'port' (de 1 a 65535).");
        string name = (ReadString(s["name"]) ?? "").Trim();
        if (name.Length > EngineConfig.MaxStreamServerName)
            throw new BadParamsException($"El nom del servidor pot tenir com a molt {EngineConfig.MaxStreamServerName} caràcters.");
        if (name.Length == 0) name = host;
        string? password = s["password"] is null ? null : ReadString(s["password"]) ?? throw new BadParamsException("'password' ha de ser un text.");
        string mount = (ReadString(s["mount"]) ?? "").Trim();
        if (mount.Length == 0) mount = "/stream";
        if (!mount.StartsWith('/')) mount = "/" + mount;
        string user = (ReadString(s["user"]) ?? "").Trim();
        if (user.Length == 0) user = "source";
        int streamId = OptionalInt(s["streamId"], "streamId", 1, 999) ?? 1;

        string savedId = "";
        config.Update(c =>
        {
            var existing = id is null ? null : c.StreamServers.FirstOrDefault(x => x.Id == id);
            if (id is not null && existing is null)
                throw new BadParamsException($"No hi ha cap servidor '{id}'.");
            if (existing is null && c.StreamServers.Count >= EngineConfig.MaxStreamServers)
                throw new BadParamsException($"Hi pot haver com a molt {EngineConfig.MaxStreamServers} servidors.");
            if (existing is not null && existing.Id == c.Stream.ServerId && _state.Streamer.IsActive)
                throw new StreamerCommandException("No es pot canviar el servidor mentre s'hi emet. Atura l'emissió primer.");

            savedId = existing?.Id ?? NextStreamServerId(c.StreamServers);
            var server = new StreamServerSettings(savedId, name, type, host, port, password ?? existing?.Password ?? "", mount, user, streamId);
            if (existing is null) c.StreamServers.Add(server);
            else c.StreamServers[c.StreamServers.IndexOf(existing)] = server;
            if (c.Stream.ServerId is null) c.Stream = c.Stream with { ServerId = savedId };
        });
        ApplyStreamServer(config, _state.Streamer);

        var result = StreamResult();
        result["serverId"] = savedId;
        return result;
    }

    /// <summary>"serverId": el treu de la llista. Si era el triat, es tria el primer que quedi. No el que s'emet.</summary>
    private JsonObject StreamDeleteServer(JsonObject msg)
    {
        var config = RequireConfig();
        string id = RequireString(msg, "serverId");
        config.Update(c =>
        {
            var existing = c.StreamServers.FirstOrDefault(x => x.Id == id)
                ?? throw new BadParamsException($"No hi ha cap servidor '{id}'.");
            if (existing.Id == c.Stream.ServerId && _state.Streamer.IsActive)
                throw new StreamerCommandException("No es pot esborrar el servidor on s'està emetent.");
            c.StreamServers.Remove(existing);
            if (c.Stream.ServerId == id) c.Stream = c.Stream with { ServerId = c.StreamServers.FirstOrDefault()?.Id };
        });
        ApplyStreamServer(config, _state.Streamer);
        return StreamResult();
    }

    /// <summary>"serverId": el servidor on s'emetrà. Només amb l'emissió aturada.</summary>
    private JsonObject StreamSelectServer(JsonObject msg)
    {
        var config = RequireConfig();
        string id = RequireString(msg, "serverId");
        if (_state.Streamer.IsActive)
            throw new StreamerCommandException("No es pot canviar el servidor mentre s'emet.");
        config.Update(c =>
        {
            if (c.StreamServers.All(x => x.Id != id))
                throw new BadParamsException($"No hi ha cap servidor '{id}'.");
            c.Stream = c.Stream with { ServerId = id };
        });
        ApplyStreamServer(config, _state.Streamer);
        return StreamResult();
    }

    /// <summary>Opcionals "bitrateKbps", "sampleRate" i "mono" (els que no hi són es conserven). Només amb l'emissió aturada.</summary>
    private JsonObject StreamSetQuality(JsonObject msg)
    {
        var streamer = _state.Streamer;
        int bitrate = OptionalInt(msg["bitrateKbps"], "bitrateKbps", 1, 1000) ?? streamer.BitrateKbps;
        int rate = OptionalInt(msg["sampleRate"], "sampleRate", 1, 200_000) ?? streamer.SampleRate;
        bool mono = msg["mono"] is null ? streamer.Mono
            : msg["mono"] is JsonValue v && v.TryGetValue(out bool m) ? m : throw new BadParamsException("'mono' ha de ser true o false.");
        if (!Streamer.Bitrates.Contains(bitrate))
            throw new BadParamsException($"'bitrateKbps' ha de ser un d'aquests: {string.Join(", ", Streamer.Bitrates)}.");
        if (!Streamer.SampleRates.Contains(rate))
            throw new BadParamsException($"'sampleRate' ha de ser un d'aquests: {string.Join(", ", Streamer.SampleRates)}.");

        streamer.SetQuality(bitrate, rate, mono);
        _config?.Update(c => c.Stream = c.Stream with { BitrateKbps = bitrate, SampleRate = rate, Mono = mono });
        return StreamResult();
    }

    /// <summary>"title": el que veuen els oients (buit = cap). Si s'emet, s'envia de seguida.</summary>
    private JsonObject StreamSetTitle(JsonObject msg)
    {
        string title = (ReadString(msg["title"]) ?? throw new BadParamsException("Falta 'title' (un text; buit = cap títol).")).Trim();
        if (title.Length > EngineConfig.MaxStreamTitle)
            throw new BadParamsException($"El títol pot tenir com a molt {EngineConfig.MaxStreamTitle} caràcters.");
        _state.Streamer.SetTitle(title);
        _config?.Update(c => c.Stream = c.Stream with { Title = _state.Streamer.Title });
        return StreamResult();
    }

    /// <summary>"db": fader de −60 (silenci) a +12. Fora de marge, al límit. S'aplica de seguida.</summary>
    private JsonObject StreamSetGain(JsonObject msg)
    {
        if (msg["db"] is not JsonValue v || !v.TryGetValue(out double db) || double.IsNaN(db))
            throw new BadParamsException("'db' ha de ser un nombre (de −60 a +12).");
        _state.Streamer.SetGain(db);
        _config?.Update(c => c.Stream = c.Stream with { GainDb = _state.Streamer.GainDb });
        return StreamResult();
    }

    /// <summary>"enabled": anivellador (NIV). L'objectiu i el màxim són els dels reproductors (settings.normalization i settings.leveler).</summary>
    private JsonObject StreamSetLeveler(JsonObject msg)
    {
        if (msg["enabled"] is not JsonValue v || !v.TryGetValue(out bool enabled))
            throw new BadParamsException("'enabled' ha de ser true o false.");
        var (target, maxBoost) = _config?.Read(c => (c.LoudnessTargetLufs, c.LevelerMaxBoostDb))
            ?? (EngineConfig.DefaultLoudnessTargetLufs, Leveler.DefaultMaxBoostDb);
        _state.Streamer.SetLeveler(enabled, target, maxBoost);
        _config?.Update(c => c.Stream = c.Stream with { Leveler = enabled });
        return StreamResult();
    }

    /// <summary>"processing" (els camps a canviar, com a player.setProcessing) o "preset". S'aplica de seguida.</summary>
    private JsonObject StreamSetProcessing(JsonObject msg)
    {
        var streamer = _state.Streamer;
        DynamicsSettings settings;
        if (ReadString(msg["preset"]) is { } presetId)
        {
            settings = AllPresets().FirstOrDefault(p => p.Preset.Id == presetId).Preset?.Settings
                ?? throw new BadParamsException($"No hi ha cap preset «{presetId}».");
        }
        else if (msg["processing"] is JsonObject processing)
        {
            settings = ConfigRepository.ReadDynamics(processing, streamer.Dynamics);
        }
        else
        {
            throw new BadParamsException("Falta 'processing' (els ajustos) o 'preset'.");
        }

        streamer.SetDynamics(settings);
        _config?.Update(c => c.Stream = c.Stream with { Dynamics = streamer.Dynamics });
        return StreamResult();
    }

    /// <summary>"enabled" i, opcional, "ceilingDb" (de −6 a 0; sense, es conserva). S'aplica de seguida.</summary>
    private JsonObject StreamSetLimiter(JsonObject msg)
    {
        if (msg["enabled"] is not JsonValue v || !v.TryGetValue(out bool enabled))
            throw new BadParamsException("'enabled' ha de ser true o false.");
        var streamer = _state.Streamer;
        double ceiling = OptionalNumber(msg["ceilingDb"], "ceilingDb", Recorder.MinLimiterCeilingDb, Recorder.MaxLimiterCeilingDb)
            ?? streamer.LimiterCeilingDb;
        streamer.SetLimiter(enabled, ceiling);
        _config?.Update(c => c.Stream = c.Stream with { Limiter = enabled, LimiterCeilingDb = streamer.LimiterCeilingDb });
        return StreamResult();
    }

    /// <summary>Comença a emetre (es connecta en segon pla; la resposta no espera la xarxa).</summary>
    private JsonObject StreamStart()
    {
        _state.Streamer.Start();
        if (_state.Streamer.State == StreamerState.Error)
            throw new StreamFailedException(_state.Streamer.ErrorMessage ?? "No s'ha pogut començar a emetre.");
        return StreamResult();
    }

    /// <summary>Atura l'emissió (la interfície ja ha demanat confirmació).</summary>
    private JsonObject StreamStop()
    {
        _state.Streamer.Stop();
        return StreamResult();
    }

    /// <summary>L'ordre ha deixat l'emissor en estat d'error: es respon amb "stream_error".</summary>
    private sealed class StreamFailedException(string message) : Exception(message);

    /// <summary>
    /// Aplica a l'emissor tot el que hi ha al perfil: servidor, qualitat, títol, fader, NIV, DSP i LIM (en arrencar el
    /// motor). La font la posa l'assignació de maquinari (4.4).
    /// </summary>
    public static void ApplyStreamSettings(ConfigStore config, Streamer streamer)
    {
        var (s, target, maxBoost) = config.Read(c => (c.Stream, c.LoudnessTargetLufs, c.LevelerMaxBoostDb));
        ApplyStreamServer(config, streamer);
        streamer.SetQuality(s.BitrateKbps, s.SampleRate, s.Mono);
        streamer.SetTitle(s.Title);
        streamer.SetGain(s.GainDb);
        streamer.SetLeveler(s.Leveler, target, maxBoost);
        streamer.SetDynamics(s.Dynamics);
        streamer.SetLimiter(s.Limiter, s.LimiterCeilingDb);
    }

    /// <summary>Passa a l'emissor el servidor triat al perfil (si no s'està emetent).</summary>
    private static void ApplyStreamServer(ConfigStore config, Streamer streamer)
    {
        if (streamer.IsActive) return;
        var selected = config.Read(c => c.StreamServers.FirstOrDefault(x => x.Id == c.Stream.ServerId));
        streamer.SetServer(selected?.ToServer(), selected is null ? null : new StreamInfo { Name = selected.Name });
    }

    /// <summary>
    /// L'adreça del servidor sense «http://», sense camí i sense espais (es pot enganxar la de l'oient). El port va a part.
    /// </summary>
    private static string CleanStreamHost(string? text)
    {
        string host = (text ?? "").Trim();
        int scheme = host.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) host = host[(scheme + 3)..];
        int slash = host.IndexOfAny(['/', ';', '?']);
        if (slash >= 0) host = host[..slash];
        if (host.Length == 0)
            throw new BadParamsException("Falta 'host' (l'adreça del servidor, p. ex. «radio.example.com»).");
        if (host.Contains(':'))
            throw new BadParamsException("Posa el port a part, no a l'adreça (p. ex. adreça «radio.example.com» i port 8000).");
        if (host.Any(char.IsWhiteSpace) || host.Length > 253)
            throw new BadParamsException("L'adreça del servidor no és vàlida.");
        return host;
    }

    private static string NextStreamServerId(IEnumerable<StreamServerSettings> servers) =>
        ((servers.Select(s => int.TryParse(s.Id, out int n) ? n : 0).DefaultIfEmpty(0).Max()) + 1).ToString();
}
