using OnaBages.Engine.Data;

namespace OnaBages.Engine;

/// <summary>Què mostra una pestanya de l'explorador: àudios o llistes desades.</summary>
public enum ExplorerKind { Audios, Lists }

/// <summary>Una entrada d'una carpeta: subcarpeta o fitxer.</summary>
public sealed record ExplorerEntry(string Name, string Path, long Size, DateTime Modified);

/// <summary>Contingut d'una carpeta. <paramref name="Truncated"/>: n'hi havia més de <see cref="ExplorerBrowser.MaxEntries"/>.</summary>
public sealed record ExplorerListing(string Path, IReadOnlyList<ExplorerEntry> Folders, IReadOnlyList<ExplorerEntry> Files, bool Truncated);

/// <summary>Error de l'explorador. <paramref name="Code"/> és el codi d'error del protocol.</summary>
public sealed class ExplorerException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Llegeix el contingut d'una carpeta de l'explorador del panell lateral (fase 4, pas 2a; D-063):
/// les subcarpetes i els fitxers d'àudio (o de llista), sense baixar a les subcarpetes.
///
/// Només es pot llegir dins de les carpetes configurades al perfil: la interfície no pot fer
/// llegir al motor cap altra carpeta de l'ordinador.
/// </summary>
public static class ExplorerBrowser
{
    /// <summary>Com a molt, tants fitxers i tantes subcarpetes per carpeta (la resta no es mostra).</summary>
    public const int MaxEntries = 5000;

    private static readonly string[] ListExtensions = [PlaylistFiles.Extension, ".m3u", ".m3u8"];

    /// <summary>Extensions que es mostren a cada pestanya.</summary>
    public static IReadOnlyList<string> Extensions(ExplorerKind kind) =>
        kind == ExplorerKind.Audios ? PlaylistFiles.AudioExtensions : ListExtensions;

    /// <summary>
    /// Contingut de <paramref name="path"/>, que ha de ser una de les <paramref name="roots"/> o una subcarpeta seva.
    /// </summary>
    /// <exception cref="ExplorerException">bad_params si és fora de les carpetes; file_error si no es pot llegir.</exception>
    public static ExplorerListing List(IEnumerable<ExplorerFolder> roots, string path, ExplorerKind kind)
    {
        string full;
        try
        {
            full = System.IO.Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ExplorerException("bad_params", "La ruta no és vàlida.");
        }
        if (!IsInside(roots, full))
            throw new ExplorerException("bad_params", "Aquesta carpeta no és de l'explorador d'aquest perfil.");
        if (!Directory.Exists(full))
            throw new ExplorerException("file_error", $"No es troba la carpeta {full}. Potser és en un disc o una unitat de xarxa que ara no hi és.");

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            RecurseSubdirectories = false,
        };
        var extensions = Extensions(kind);
        try
        {
            var dir = new DirectoryInfo(full);
            var folders = dir.EnumerateDirectories("*", options)
                .Take(MaxEntries + 1)
                .Select(d => new ExplorerEntry(d.Name, d.FullName, 0, d.LastWriteTime))
                .ToList();
            var files = dir.EnumerateFiles("*", options)
                .Where(f => extensions.Contains(f.Extension.ToLowerInvariant()))
                .Take(MaxEntries + 1)
                .Select(f => new ExplorerEntry(f.Name, f.FullName, f.Length, f.LastWriteTime))
                .ToList();

            bool truncated = folders.Count > MaxEntries || files.Count > MaxEntries;
            return new ExplorerListing(
                full,
                folders.Take(MaxEntries).OrderBy(e => e.Name, PlaylistFiles.NaturalComparer.Instance).ToList(),
                files.Take(MaxEntries).OrderBy(e => e.Name, PlaylistFiles.NaturalComparer.Instance).ToList(),
                truncated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new ExplorerException("file_error", $"No s'ha pogut llegir la carpeta {full}: {ex.Message}");
        }
    }

    /// <summary>
    /// <paramref name="fullPath"/> és una de les carpetes, o és a dins d'alguna (comparant carpeta a carpeta:
    /// «D:\Música2» no és dins de «D:\Música»).
    /// </summary>
    public static bool IsInside(IEnumerable<ExplorerFolder> roots, string fullPath)
    {
        string target = WithSeparator(fullPath);
        foreach (var root in roots)
        {
            string rootPath;
            try { rootPath = WithSeparator(System.IO.Path.GetFullPath(root.Path)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (target.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string WithSeparator(string path) =>
        System.IO.Path.EndsInDirectorySeparator(path) ? path : path + System.IO.Path.DirectorySeparatorChar;
}
