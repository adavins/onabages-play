using System.Text.Json.Nodes;

namespace OnaBages.Engine.Protocol;

/// <summary>Conversió del resum del maquinari al format del protocol (docs/protocol.md, 4.4 i 5.4).</summary>
public static class HardwareJson
{
    public static JsonObject Report(HardwareReport report)
    {
        var formatChanges = new JsonArray();
        foreach (var change in report.FormatChanges)
        {
            var device = DeviceJson.Device(change.Device);
            formatChanges.Add(new JsonObject
            {
                ["device"] = device,
                ["previousFormat"] = new JsonObject
                {
                    ["sampleRate"] = change.PreviousFormat!.SampleRate,
                    ["bits"] = change.PreviousFormat.Bits,
                    ["channels"] = change.PreviousFormat.Channels,
                },
            });
        }

        return new JsonObject
        {
            ["hasChanges"] = report.HasChanges,
            ["firstRun"] = report.FirstRun,
            ["configProblem"] = report.ConfigProblem,
            ["elements"] = new JsonArray(report.Elements.Select(e => (JsonNode)Element(e)).ToArray()),
            ["newDevices"] = new JsonArray(report.NewDevices.Select(d => (JsonNode)DeviceJson.Device(d)).ToArray()),
            ["formatChanges"] = formatChanges,
        };
    }

    /// <summary>Una línia del resum: l'element, el dispositiu desat, el que s'ha trobat i què ha passat.</summary>
    public static JsonObject Element(ElementCheck check) => new()
    {
        ["element"] = check.Kind switch { ElementKind.Player => "player", ElementKind.Recorder => "recorder", ElementKind.Cart => "cart", ElementKind.Streamer => "stream", _ => "cue" },
        ["id"] = check.ElementId,
        ["result"] = Result(check.Result),
        ["saved"] = new JsonObject { ["id"] = check.Saved.Id, ["name"] = check.Saved.Name },
        ["device"] = check.Device is null ? null : DeviceJson.Device(check.Device),
    };

    /// <summary>Esdeveniment <c>hardware.reassigned</c>: ha tornat el dispositiu d'un element.</summary>
    public static JsonObject ReassignedEvent(ElementCheck check)
    {
        var ev = Messages.Event("hardware.reassigned");
        ev["element"] = Element(check);
        return ev;
    }

    public static string Result(CheckResult result) => result switch
    {
        CheckResult.Ok => "ok",
        CheckResult.Unplugged => "unplugged",
        CheckResult.Disabled => "disabled",
        CheckResult.IdChanged => "idChanged",
        CheckResult.Ambiguous => "ambiguous",
        _ => "missing",
    };
}
