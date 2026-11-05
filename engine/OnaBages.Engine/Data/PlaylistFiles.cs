using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OnaBages.Audio;

namespace OnaBages.Engine.Data;

/// <summary>Una llista no es pot llegir o escriure. El missatge és per a l'operador.</summary>
public sealed class PlaylistFileException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Fitxers de llista (pas 5 de la fase 2, D-035): el format propi <c>.obpl</c> (JSON) i la
/// importació de <c>.m3u</c> / <c>.m3u8</c>. També l'última llista de cada reproductor, que es
/// desa sola i es recupera en arrencar.
///
/// Forma part del mòdul de dades: cap altre codi llegeix ni escriu llistes.
/// Els fitxers d'àudio no es copien mai: només se'n guarden les rutes.
/// </summary>
public static class PlaylistFiles
{
    public const int SchemaVersion = 1;
    public const string Extension = ".obpl";

    /// <summary>És un fitxer de llista (propi o M3U), no un fitxer d'àudio?</summary>
    public static bool IsPlaylistFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is Extension or ".m3u" or ".m3u8";

    /// <summary>Llegeix una llista (.obpl, .m3u o .m3u8).</summary>
    /// <exception cref="PlaylistFileException">Si no existeix o no es pot llegir.</exception>
    public static IReadOnlyList<PlaylistEntry> Read(string path)
    {
        if (!File.Exists(path))
            throw new PlaylistFileException($"No es troba la llista: {path}");

        try
        {
            return Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase)
                ? ReadObpl(path)
                : ReadM3u(path);
        }
        catch (PlaylistFileException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PlaylistFileException($"No s'ha pogut llegir la llista {Path.GetFileName(path)}: el fitxer és malmès o no és una llista.", ex);
        }
    }

    /// <summary>Desa una llista en format .obpl (escriptura atòmica).</summary>
    /// <exception cref="PlaylistFileException">Si no es pot escriure.</exception>
    public static void Write(string path, IEnumerable<PlaylistItem> items)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var list = new JsonArray();
        foreach (var item in items)
        {
            if (item.IsSection)
            {
                // Separador de secció (pas 9c): no té cap fitxer.
                list.Add(new JsonObject
                {
                    ["section"] = new JsonObject { ["title"] = item.Name, ["color"] = item.Color, ["collapsed"] = item.Collapsed },
                });
                continue;
            }
            list.Add(new JsonObject
            {
                ["path"] = item.Path,
                // Si es mou la llista juntament amb la música, es troba igualment.
                ["relativePath"] = Path.GetRelativePath(folder, item.Path),
                ["loop"] = item.Loop,
                ["autoNext"] = item.AutoNext,
            });
        }

        var root = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["savedAt"] = DateTimeOffset.Now.ToString("O"),
            ["items"] = list,
        };

        try
        {
            AtomicFile.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PlaylistFileException($"No s'ha pogut desar la llista a {path}: {ex.Message}", ex);
        }
    }

    /// <summary>Formats d'àudio que el motor pot llegir (especificació 6.3).</summary>
    public static readonly string[] AudioExtensions = [".mp3", ".wav", ".m4a", ".aac", ".flac", ".wma", ".ogg"];

    /// <summary>
    /// Fitxers d'àudio d'una carpeta i de les seves subcarpetes, en l'ordre de l'Explorador:
    /// primer els fitxers de la carpeta per nom («2» abans de «10»), després cada subcarpeta.
    /// Se salten els fitxers ocults o de sistema i les carpetes sense permís.
    /// </summary>
    public static List<string> AudioFilesInFolder(string folder)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        var result = new List<string>();
        AddFolder(folder, options, result);
        return result;
    }

    private static void AddFolder(string folder, EnumerationOptions options, List<string> result)
    {
        var files = Directory.EnumerateFiles(folder, "*", options)
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(Path.GetFileName, NaturalComparer.Instance);
        result.AddRange(files);

        foreach (string sub in Directory.EnumerateDirectories(folder, "*", options).OrderBy(Path.GetFileName, NaturalComparer.Instance))
            AddFolder(sub, options, result);
    }

    /// <summary>Compara noms com l'Explorador: els números es comparen pel seu valor («Pista 2» &lt; «Pista 10»).</summary>
    private sealed class NaturalComparer : IComparer<string?>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? a, string? b)
        {
            a ??= ""; b ??= "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
                {
                    // Agafa tot el número de cada banda i compara'ls pel valor (sense zeros a l'esquerra).
                    int si = i, sj = j;
                    while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                    while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                    string na = a[si..i].TrimStart('0'), nb = b[sj..j].TrimStart('0');
                    int byValue = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                    if (byValue != 0) return byValue;
                }
                else
                {
                    int c = string.Compare(a, i, b, j, 1, StringComparison.CurrentCultureIgnoreCase);
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }

    /// <summary>
    /// Fitxer on es desa sola l'última llista d'un reproductor, dins de la carpeta de llistes
    /// del perfil actiu (<see cref="ConfigRepository.LastListsFolder"/>).
    /// </summary>
    public static string LastListPath(string folder, string playerId) => Path.Combine(folder, $"reproductor-{playerId}{Extension}");

    // -----------------------------------------------------------------------

    private static List<PlaylistEntry> ReadObpl(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new FormatException("no és un objecte JSON");
        int? version = root["schemaVersion"] is JsonValue v && v.TryGetValue(out int i) ? i : null;
        if (version != SchemaVersion)
            throw new PlaylistFileException($"La llista {Path.GetFileName(path)} és d'una versió que aquest programa no coneix ({version?.ToString() ?? "sense versió"}).");

        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var entries = new List<PlaylistEntry>();
        foreach (var node in root["items"] as JsonArray ?? [])
        {
            // Separador de secció (pas 9c; camp opcional, sense canvi de versió).
            if (node?["section"] is JsonObject section)
            {
                entries.Add(PlaylistEntry.Section(Text(section["title"]) ?? Playlist.DefaultSectionTitle,
                    Text(section["color"]), Bool(section["collapsed"])));
                continue;
            }

            string? absolute = Text(node?["path"]);
            string? relative = Text(node?["relativePath"]);
            if (absolute is null && relative is null) continue;

            // Primer la ruta completa; si ja no hi és, la relativa a on és ara la llista.
            string chosen = absolute ?? Path.GetFullPath(Path.Combine(folder, relative!));
            if (!File.Exists(chosen) && relative is not null)
            {
                string fromList = Path.GetFullPath(Path.Combine(folder, relative));
                if (File.Exists(fromList)) chosen = fromList;
            }

            entries.Add(PlaylistEntry.ForFile(chosen, Bool(node?["loop"]), Bool(node?["autoNext"])));
        }
        return entries;
    }

    /// <summary>
    /// M3U: una ruta per línia; les línies que comencen per # són comentaris o informació (#EXTINF).
    /// Les rutes relatives ho són a la carpeta de la llista. Els .m3u antics poden no ser UTF-8.
    /// </summary>
    private static List<PlaylistEntry> ReadM3u(string path)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var entries = new List<PlaylistEntry>();
        foreach (string raw in ReadText(path).Split('\n'))
        {
            string line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                line = Uri.UnescapeDataString(line["file:///".Length..]).Replace('/', '\\');
            else if (line.Contains("://"))
                continue; // adreces d'internet: no són fitxers

            string full = Path.IsPathFullyQualified(line) ? line : Path.GetFullPath(Path.Combine(folder, line));
            entries.Add(PlaylistEntry.ForFile(full)); // «______.mp3» del Winamp: separador de secció
        }
        return entries;
    }

    /// <summary>Text del fitxer: UTF-8 si ho és; si no, Latin-1 (els .m3u antics de Windows).</summary>
    private static string ReadText(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static bool Bool(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out bool b) && b;
}
