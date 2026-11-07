using OnaBages.Audio;
using OnaBages.Engine.Data;
using System.Text.Json.Nodes;

namespace OnaBages.Engine.Protocol;

/// <summary>JSON de l'editor (docs/protocol.md, 4.10).</summary>
public static class EditorJson
{
    /// <param name="output">Sortida triada per a l'editor (null = la de la preescolta).</param>
    public static JsonObject Editor(EditorSession editor, DeviceRef? output = null)
    {
        var edit = editor.Edit;
        var index = edit?.Index;
        var player = editor.Player;
        return new JsonObject
        {
            // Reproducció de l'editor (pas 3).
            ["playback"] = new JsonObject
            {
                ["state"] = player.State switch
                {
                    EditorPlayState.Playing => "playing",
                    EditorPlayState.Error => "error",
                    _ => "stopped",
                },
                ["position"] = Math.Round(player.Position, 3),
                ["error"] = player.ErrorMessage,
                ["output"] = output is null ? null : new JsonObject { ["id"] = output.Id, ["name"] = output.Name },
            },
            ["open"] = editor.Path is not null,
            ["path"] = editor.Path,
            ["busy"] = editor.Busy,
            ["progress"] = Math.Round(editor.Progress, 3),
            ["error"] = editor.Error,
            ["savedPath"] = editor.SavedPath,
            // Desar la selecció (fase 5, pas 23d): on s'ha desat l'última (l'edició no canvia).
            ["savedSelectionPath"] = editor.SavedSelectionPath,
            // La resta, només quan el fitxer ja és obert.
            ["sampleRate"] = index?.SampleRate,
            ["frameSeconds"] = index?.FrameSeconds,
            ["bitrates"] = index is null ? null : new JsonArray(index.Bitrates.Select(b => (JsonNode)b).ToArray()),
            ["originalFrames"] = index?.FrameCount,
            ["originalDuration"] = index is null ? null : Math.Round(index.Duration.TotalSeconds, 3),
            ["duration"] = edit is null ? null : Math.Round(edit.Duration.TotalSeconds, 3),
            ["frames"] = edit?.FrameCount,
            // Trossos de l'original que es queden: [primer fotograma, quants].
            ["segments"] = edit is null ? null : new JsonArray(edit.Segments
                .Select(s => (JsonNode)new JsonArray(s.Start, s.Count)).ToArray()),
            // Forma d'ona: quants fotogrames de l'original ja en tenen (editor.peaks); quan és igual
            // a index.FrameCount, és completa.
            ["peaksDone"] = edit is null ? 0 : editor.PeaksDone,
            ["peaksError"] = editor.PeaksError,
            ["canUndo"] = edit?.CanUndo ?? false,
            ["canRedo"] = edit?.CanRedo ?? false,
            ["modified"] = edit?.IsModified ?? false,
            // Fosos (fase 4, pas 6b): [fotograma de l'original, passos del grànul 1, passos del grànul 2], en ordre.
            // Cada pas és de 1,5 dB (negatiu = més fluix); silenceSteps o menys = silenci.
            ["gains"] = edit is null ? null : new JsonArray(edit.Gains.OrderBy(g => g.Key)
                .Select(g => (JsonNode)new JsonArray(g.Key, g.Value.Granule0, g.Value.Granule1)).ToArray()),
            ["silenceSteps"] = Mp3Edit.SilenceSteps,
            // Marques de l'enregistrament (fase 5, pas 23c), on són a la versió editada (segons). Les dels trossos
            // eliminats no hi surten; number és el de l'original (no canvia en retallar).
            ["marks"] = new JsonArray(editor.EditedMarks().Select(m => (JsonNode)new JsonObject
            {
                ["number"] = m.Number,
                ["seconds"] = Math.Round(m.Mark.Seconds, 3),
                ["name"] = m.Mark.Name,
            }).ToArray()),
            ["granulesPerFrame"] = edit?.GranulesPerFrame,
        };
    }

    /// <summary>Pics de la forma d'ona (en base64: 2 bytes per fotograma, esquerre i dret, de 0 a 255).</summary>
    public static JsonObject Peaks(int start, byte[] data) => new()
    {
        ["start"] = start,
        ["count"] = data.Length / Audio.WaveformPeaks.BytesPerFrame,
        ["data"] = Convert.ToBase64String(data),
    };

    public static JsonObject ChangedEvent(EditorSession editor, DeviceRef? output)
    {
        var ev = Messages.Event("editor.changed");
        ev["editor"] = Editor(editor, output);
        return ev;
    }

    /// <summary>Posició de la reproducció de l'editor (10 per segon mentre sona).</summary>
    public static JsonObject PositionEvent(EditorSession editor)
    {
        var ev = Messages.Event("editor.position");
        ev["position"] = Math.Round(editor.Player.Position, 3);
        return ev;
    }
}
