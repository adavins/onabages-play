using System.Text.Json.Nodes;

namespace OnaBages.Engine.Protocol;

/// <summary>JSON de l'editor (docs/protocol.md, 4.10).</summary>
public static class EditorJson
{
    public static JsonObject Editor(EditorSession editor)
    {
        var edit = editor.Edit;
        var index = edit?.Index;
        return new JsonObject
        {
            ["open"] = editor.Path is not null,
            ["path"] = editor.Path,
            ["busy"] = editor.Busy,
            ["progress"] = Math.Round(editor.Progress, 3),
            ["error"] = editor.Error,
            ["savedPath"] = editor.SavedPath,
            // La resta, només quan el fitxer ja és obert.
            ["sampleRate"] = index?.SampleRate,
            ["frameSeconds"] = index?.FrameSeconds,
            ["bitrates"] = index is null ? null : new JsonArray(index.Bitrates.Select(b => (JsonNode)b).ToArray()),
            ["originalDuration"] = index is null ? null : Math.Round(index.Duration.TotalSeconds, 3),
            ["duration"] = edit is null ? null : Math.Round(edit.Duration.TotalSeconds, 3),
            ["frames"] = edit?.FrameCount,
            // Trossos de l'original que es queden: [primer fotograma, quants].
            ["segments"] = edit is null ? null : new JsonArray(edit.Segments
                .Select(s => (JsonNode)new JsonArray(s.Start, s.Count)).ToArray()),
            ["canUndo"] = edit?.CanUndo ?? false,
            ["canRedo"] = edit?.CanRedo ?? false,
            ["modified"] = edit?.IsModified ?? false,
        };
    }

    public static JsonObject ChangedEvent(EditorSession editor)
    {
        var ev = Messages.Event("editor.changed");
        ev["editor"] = Editor(editor);
        return ev;
    }
}
