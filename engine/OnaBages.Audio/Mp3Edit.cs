namespace OnaBages.Audio;

/// <summary>Tros del fitxer original que es queda: <paramref name="Count"/> fotogrames a partir de <paramref name="Start"/>.</summary>
public readonly record struct FrameRange(int Start, int Count)
{
    public int End => Start + Count;
}

/// <summary>
/// Edició d'un MP3 sense pèrdua (especificació 9.3): no toca mai l'original mentre s'edita.
/// Només guarda quins trossos (en fotogrames) es queden. Eliminar o retallar és treure'n un tros;
/// desfer i refer és tornar a una llista anterior; desar és copiar els fotogrames que queden.
///
/// Totes les posicions són de la versió <b>editada</b> (la que es veu a l'editor), en fotogrames.
/// No és segura entre fils: qui la fa servir (el motor) l'ha de cridar des d'un sol fil o amb un bloqueig.
/// </summary>
public sealed class Mp3Edit
{
    /// <summary>Quants passos es poden desfer.</summary>
    public const int MaxUndo = 200;

    private readonly List<IReadOnlyList<FrameRange>> _undo = [];
    private readonly List<IReadOnlyList<FrameRange>> _redo = [];

    public Mp3Edit(Mp3FrameIndex index)
    {
        Index = index;
        Segments = [new FrameRange(0, index.FrameCount)];
    }

    public Mp3FrameIndex Index { get; }

    /// <summary>Trossos de l'original que es queden, en ordre.</summary>
    public IReadOnlyList<FrameRange> Segments { get; private set; }

    /// <summary>Fotogrames de la versió editada.</summary>
    public int FrameCount => Segments.Sum(s => s.Count);

    public TimeSpan Duration => TimeSpan.FromSeconds(FrameCount * Index.FrameSeconds);

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Hi ha canvis respecte del fitxer original.</summary>
    public bool IsModified => Segments.Count != 1 || Segments[0] != new FrameRange(0, Index.FrameCount);

    /// <summary>Fotograma més proper a un temps de la versió editada (dins de 0..FrameCount).</summary>
    public int FrameAt(double seconds) =>
        (int)Math.Clamp(Math.Round(seconds / Index.FrameSeconds), 0, FrameCount);

    /// <summary>Temps on comença un fotograma de la versió editada.</summary>
    public double SecondsAt(int frame) => frame * Index.FrameSeconds;

    /// <summary>Fotograma de l'original que hi ha en aquest punt de la versió editada (per reproduir i per a la forma d'ona).</summary>
    public int ToOriginal(int frame)
    {
        foreach (var s in Segments)
        {
            if (frame < s.Count) return s.Start + frame;
            frame -= s.Count;
        }
        return Index.FrameCount; // final
    }

    /// <summary>Trossos de l'original que hi ha entre <paramref name="start"/> i <paramref name="end"/> de la versió editada (per reproduir-los).</summary>
    public IReadOnlyList<FrameRange> SegmentsBetween(int start, int end)
    {
        (start, end) = Clamp(start, end);
        return Slice(Segments, start, end);
    }

    /// <summary>Elimina els fotogrames [start, end) de la versió editada. Retorna false si no hi ha res a eliminar.</summary>
    /// <exception cref="Mp3EditException">Si s'eliminaria tot.</exception>
    public bool Delete(int start, int end)
    {
        (start, end) = Clamp(start, end);
        if (end <= start) return false;
        if (start == 0 && end == FrameCount)
            throw new Mp3EditException("No es pot eliminar tot el fitxer.");

        var result = new List<FrameRange>();
        int pos = 0; // posició a la versió editada on comença cada tros
        foreach (var s in Segments)
        {
            int segStart = pos, segEnd = pos + s.Count;
            pos = segEnd;
            // Part del tros abans de la selecció i part de després.
            if (segStart < start)
                result.Add(new FrameRange(s.Start, Math.Min(segEnd, start) - segStart));
            if (segEnd > end)
            {
                int skip = Math.Max(0, end - segStart);
                result.Add(new FrameRange(s.Start + skip, s.Count - skip));
            }
        }
        Apply(Merge(result));
        return true;
    }

    /// <summary>Es queda només amb [start, end) (retallar l'inici i el final d'un sol cop).</summary>
    public bool Keep(int start, int end)
    {
        (start, end) = Clamp(start, end);
        if (end <= start)
            throw new Mp3EditException("Cal seleccionar el fragment que es vol conservar.");
        if (start == 0 && end == FrameCount) return false;

        // Com retallar l'inici i el final, però en un sol pas de desfer.
        Apply(Merge(Slice(Segments, start, end)));
        return true;
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Add(Segments);
        Segments = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Add(Segments);
        Segments = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        return true;
    }

    /// <summary>
    /// Desa la versió editada a <paramref name="target"/>: l'ID3v2, els fotogrames que es queden i l'ID3v1.
    /// S'escriu primer en un fitxer temporal a la mateixa carpeta, es força al disc i després ocupa el
    /// lloc del de destinació: si alguna cosa falla, el fitxer de destinació (o l'original) queda intacte.
    /// </summary>
    /// <param name="progress">Es crida de tant en tant amb la fracció feta (0..1).</param>
    /// <exception cref="Mp3EditException">Si l'original ha canviat, no es pot llegir o no es pot escriure la destinació.</exception>
    public void Save(string target, Action<double>? progress = null, CancellationToken cancel = default)
    {
        var source = new FileInfo(Index.Path);
        if (!source.Exists)
            throw new Mp3EditException("El fitxer original ja no hi és (l'han mogut o esborrat).");
        if (source.Length != Index.FileSize || source.LastWriteTimeUtc != Index.LastWriteUtc)
            throw new Mp3EditException("El fitxer original ha canviat des que s'ha obert a l'editor. Torna'l a obrir.");

        string folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(target))!;
        string temp = System.IO.Path.Combine(folder, $".{System.IO.Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        var segments = Segments;
        long totalFrames = segments.Sum(s => (long)s.Count), done = 0;

        try
        {
            Directory.CreateDirectory(folder);
            using (var input = new FileStream(Index.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 256 * 1024))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024))
            {
                var buffer = new byte[256 * 1024];
                Copy(input, output, 0, Index.Id3v2Length, buffer);

                foreach (var s in segments)
                {
                    // Els fotogrames d'un tros són seguits a l'original: es copien d'un sol cop
                    // (per blocs de com a molt 4.096 fotogrames, per poder informar del progrés).
                    for (int f = s.Start; f < s.End; f += 4096)
                    {
                        cancel.ThrowIfCancellationRequested();
                        int last = Math.Min(s.End, f + 4096) - 1;
                        long from = Index.Offset(f);
                        long to = Index.Offset(last) + Index.Length(last);
                        Copy(input, output, from, to - from, buffer);
                        done += last - f + 1;
                        progress?.Invoke((double)done / totalFrames);
                    }
                }

                if (Index.Id3v1Offset >= 0)
                    Copy(input, output, Index.Id3v1Offset, 128, buffer);
                output.Flush(flushToDisk: true);
            }

            File.Move(temp, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            try { File.Delete(temp); } catch (Exception) { /* es queda un .tmp ocult; no és greu */ }
            if (ex is OperationCanceledException) throw;
            throw new Mp3EditException(ex is UnauthorizedAccessException || ex.HResult == unchecked((int)0x80070020)
                ? $"No s'ha pogut desar {System.IO.Path.GetFileName(target)}: el fitxer és obert en un altre lloc (p. ex. sona en un reproductor) o no s'hi pot escriure."
                : $"No s'ha pogut desar {System.IO.Path.GetFileName(target)}: {ex.Message}", ex);
        }
    }

    // -----------------------------------------------------------------------

    private void Apply(IReadOnlyList<FrameRange> segments)
    {
        _undo.Add(Segments);
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
        Segments = segments;
    }

    private (int, int) Clamp(int start, int end)
    {
        int total = FrameCount;
        start = Math.Clamp(start, 0, total);
        end = Math.Clamp(end, 0, total);
        return start <= end ? (start, end) : (end, start);
    }

    /// <summary>Els trossos de [start, end) de la versió editada.</summary>
    private static List<FrameRange> Slice(IReadOnlyList<FrameRange> segments, int start, int end)
    {
        var result = new List<FrameRange>();
        int pos = 0;
        foreach (var s in segments)
        {
            int segStart = pos, segEnd = pos + s.Count;
            pos = segEnd;
            int from = Math.Max(segStart, start), to = Math.Min(segEnd, end);
            if (to > from)
                result.Add(new FrameRange(s.Start + (from - segStart), to - from));
        }
        return result;
    }

    /// <summary>Ajunta els trossos que han quedat seguits a l'original.</summary>
    private static List<FrameRange> Merge(List<FrameRange> segments)
    {
        var result = new List<FrameRange>();
        foreach (var s in segments)
        {
            if (s.Count <= 0) continue;
            if (result.Count > 0 && result[^1].End == s.Start)
                result[^1] = new FrameRange(result[^1].Start, result[^1].Count + s.Count);
            else
                result.Add(s);
        }
        return result;
    }

    private static void Copy(Stream input, Stream output, long from, long count, byte[] buffer)
    {
        input.Position = from;
        while (count > 0)
        {
            int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0) throw new IOException("El fitxer original s'ha escurçat mentre es desava.");
            output.Write(buffer, 0, read);
            count -= read;
        }
    }
}
