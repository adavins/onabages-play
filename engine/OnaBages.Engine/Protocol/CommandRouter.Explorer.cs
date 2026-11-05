using System.Text.Json.Nodes;
using OnaBages.Engine.Data;

namespace OnaBages.Engine.Protocol;

/// <summary>
/// Ordres de l'explorador del panell lateral (fase 4, pas 2a; D-063): les carpetes de cada pestanya
/// (Àudios i Llistes, desades al perfil) i el contingut d'una carpeta.
/// </summary>
public sealed partial class CommandRouter
{
    /// <summary>{ audios: [{ name, path, exists }], lists: [...] }</summary>
    private JsonObject ExplorerGet() => ExplorerJson(RequireConfig());

    private static JsonObject ExplorerJson(ConfigStore config)
    {
        var (audios, lists) = config.Read(c => (c.AudioFolders.ToList(), c.ListFolders.ToList()));
        return new JsonObject { ["audios"] = FoldersJson(audios), ["lists"] = FoldersJson(lists) };
    }

    /// <summary>
    /// La ruta es dona normalitzada, igual que les d'explorer.list (p. ex. «C:\Users\ALBERT~1» es desplega):
    /// així la interfície pot saber on és cada carpeta dins de la seva carpeta del perfil.
    /// </summary>
    private static JsonArray FoldersJson(List<ExplorerFolder> folders) => new(folders
        .Select(f => (JsonNode)new JsonObject { ["name"] = f.Name, ["path"] = NormalizedPath(f.Path), ["exists"] = Directory.Exists(f.Path) })
        .ToArray());

    private static string NormalizedPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    /// <summary>
    /// kind ("audios" o "lists"), folders: [{ name?, path }] en l'ordre que es mostren. Substitueix les d'abans.
    /// La carpeta no ha d'existir per força (p. ex. una unitat de xarxa que ara no hi és).
    /// </summary>
    private JsonObject ExplorerSetFolders(JsonObject msg)
    {
        var config = RequireConfig();
        var kind = ReadKind(msg);
        if (msg["folders"] is not JsonArray items)
            throw new BadParamsException("Falta el camp 'folders' (una llista).");
        if (items.Count > EngineConfig.MaxExplorerFolders)
            throw new BadParamsException($"Com a molt hi pot haver {EngineConfig.MaxExplorerFolders} carpetes.");

        var folders = new List<ExplorerFolder>();
        foreach (var item in items)
        {
            string? path = ReadString(item?["path"]);
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new BadParamsException("Cada carpeta ha de tenir una ruta completa ('path').");
            path = Path.GetFullPath(path);
            if (folders.Any(f => string.Equals(Path.TrimEndingDirectorySeparator(f.Path), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase)))
                throw new BadParamsException($"La carpeta {path} hi és dues vegades.");

            string name = ConfigRepository.CleanProgramName(ReadString(item?["name"]) ?? "", cut: false);
            if (name.Length == 0)
                name = ConfigRepository.ExplorerFolderDefaultName(path);
            if (name.Length > EngineConfig.MaxExplorerFolderName)
                throw new BadParamsException($"El nom d'una carpeta pot tenir com a molt {EngineConfig.MaxExplorerFolderName} caràcters.");
            folders.Add(new ExplorerFolder(name, path));
        }

        config.Update(c =>
        {
            if (kind == ExplorerKind.Audios) c.AudioFolders = folders;
            else c.ListFolders = folders;
        });
        return ExplorerJson(config);
    }

    /// <summary>kind, path (una carpeta configurada o una subcarpeta seva). { path, folders, files, truncated }</summary>
    private JsonObject ExplorerList(JsonObject msg)
    {
        var config = RequireConfig();
        var kind = ReadKind(msg);
        string path = RequireString(msg, "path");
        var roots = config.Read(c => (kind == ExplorerKind.Audios ? c.AudioFolders : c.ListFolders).ToList());

        ExplorerListing listing;
        try
        {
            listing = ExplorerBrowser.List(roots, path, kind);
        }
        catch (ExplorerException ex) when (ex.Code == "bad_params")
        {
            throw new BadParamsException(ex.Message);
        }
        catch (ExplorerException ex)
        {
            throw new PlaylistFileException(ex.Message); // file_error
        }

        return new JsonObject
        {
            ["path"] = listing.Path,
            ["folders"] = new JsonArray(listing.Folders
                .Select(f => (JsonNode)new JsonObject { ["name"] = f.Name, ["path"] = f.Path }).ToArray()),
            ["files"] = new JsonArray(listing.Files
                .Select(f => (JsonNode)new JsonObject
                {
                    ["name"] = f.Name,
                    ["path"] = f.Path,
                    ["size"] = f.Size,
                    ["modified"] = f.Modified.ToString("yyyy-MM-ddTHH:mm:ss"),
                }).ToArray()),
            ["truncated"] = listing.Truncated,
        };
    }

    private static ExplorerKind ReadKind(JsonObject msg) => ReadString(msg["kind"]) switch
    {
        "audios" => ExplorerKind.Audios,
        "lists" => ExplorerKind.Lists,
        _ => throw new BadParamsException("'kind' ha de ser \"audios\" o \"lists\"."),
    };
}
