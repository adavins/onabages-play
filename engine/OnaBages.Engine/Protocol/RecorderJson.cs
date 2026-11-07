using System.Text.Json.Nodes;
using OnaBages.Audio;

namespace OnaBages.Engine.Protocol;

/// <summary>Conversió dels enregistradors al format del protocol (docs/protocol.md, 4.3 i 5.3).</summary>
public static class RecorderJson
{
    public static JsonObject Recorder(Recorder recorder) => new()
    {
        ["id"] = recorder.Id,
        ["state"] = State(recorder.State),
        ["name"] = recorder.Name,
        ["folder"] = recorder.Folder,
        ["gainDb"] = recorder.GainDb,
        // Limitador (fase 5, pas 9a): recorder.setLimiter; el sostre és de settings.recorderLimiter.
        ["limiter"] = new JsonObject { ["enabled"] = recorder.LimiterEnabled, ["ceilingDb"] = recorder.LimiterCeilingDb },
        ["deviceId"] = recorder.DeviceId,
        ["file"] = recorder.FilePath,
        // Còpia d'historial (fase 5, pas 10a): on és i, si ha fallat, per què (l'enregistrament continua igualment).
        ["archive"] = new JsonObject { ["path"] = recorder.ArchivePath, ["error"] = recorder.ArchiveError, ["errorDetail"] = recorder.ArchiveErrorDetail },
        ["startedAt"] = recorder.StartedAt?.ToString("O"),
        ["elapsed"] = Math.Round(recorder.Elapsed.TotalSeconds, 1),
        ["fileSize"] = recorder.FileSize,
        ["freeDiskBytes"] = recorder.FreeDiskBytes,
        ["lowDiskSpace"] = recorder.LowDiskSpace,
        ["sampleRate"] = recorder.SampleRate,
        ["bitrateKbps"] = recorder.BitrateKbps,
        ["samplesLost"] = recorder.SamplesLost,
        ["captureGlitches"] = recorder.CaptureGlitches,
        ["inputActive"] = recorder.InputActive,
        // Alarma de silenci (fase 5, pas 25a): ajustos de settings.silenceAlarm.
        ["silence"] = Silence(recorder),
        ["error"] = recorder.ErrorMessage,
        // Marques de l'enregistrament en curs o del pendent (fase 5, pas 23a): recorder.mark.
        ["marks"] = new JsonArray(recorder.Marks.Select(m => (JsonNode)new JsonObject
        {
            ["seconds"] = Math.Round(m.Seconds, 3),
            ["name"] = m.Name,
        }).ToArray()),
    };

    public static JsonArray All(IEnumerable<Recorder> recorders) =>
        new(recorders.Select(r => (JsonNode)Recorder(r)).ToArray());

    /// <summary>Esdeveniment <c>recorder.state</c>: un enregistrador ha canviat.</summary>
    public static JsonObject StateEvent(Recorder recorder)
    {
        var ev = Messages.Event("recorder.state");
        ev["recorder"] = Recorder(recorder);
        return ev;
    }

    /// <summary>Esdeveniment <c>recorder.progress</c>: temps, mida i espai dels que enregistren.</summary>
    public static JsonObject ProgressEvent(IEnumerable<Recorder> recorders)
    {
        var list = new JsonArray();
        foreach (var r in recorders.Where(r => r.State == RecorderState.Recording))
        {
            list.Add(new JsonObject
            {
                ["id"] = r.Id,
                ["elapsed"] = Math.Round(r.Elapsed.TotalSeconds, 1),
                ["fileSize"] = r.FileSize,
                ["freeDiskBytes"] = r.FreeDiskBytes,
                ["lowDiskSpace"] = r.LowDiskSpace,
                ["samplesLost"] = r.SamplesLost,
                ["captureGlitches"] = r.CaptureGlitches,
                ["silence"] = Silence(r),
            });
        }

        var ev = Messages.Event("recorder.progress");
        ev["recorders"] = list;
        return ev;
    }

    private static JsonObject Silence(Recorder r) => new()
    {
        ["alarm"] = r.SilenceAlarm,
        ["seconds"] = Math.Round(r.SilentSeconds, 1),
        ["enabled"] = r.SilenceAlarmEnabled,
        ["thresholdDb"] = r.SilenceThresholdDb,
        ["alarmSeconds"] = r.SilenceSeconds,
    };

    public static string State(RecorderState state) => state switch
    {
        RecorderState.Idle => "idle",
        RecorderState.Recording => "recording",
        RecorderState.Pending => "pending",
        _ => "error",
    };
}
