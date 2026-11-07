using System.Collections.Immutable;

namespace OnaBages.Audio;

/// <summary>Tros del fitxer original que es queda: <paramref name="Count"/> fotogrames a partir de <paramref name="Start"/>.</summary>
public readonly record struct FrameRange(int Start, int Count)
{
    public int End => Start + Count;
}

/// <summary>
/// Canvi de volum d'un fotograma de l'original (fosos, fase 4, pas 6b; D-074), en passos de
/// <see cref="Mp3Gain.StepDb"/> per a cada grànul (≤ 0; <see cref="Mp3Edit.SilenceSteps"/> = silenci).
/// A MPEG-2 només hi ha un grànul (<paramref name="Granule1"/> no es fa servir).
/// </summary>
public readonly record struct FrameGain(int Granule0, int Granule1);

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

    /// <summary>Passos que deixen un grànul en silenci (el volum global, de 0 a 255, queda a 0).</summary>
    public const int SilenceSteps = -Mp3Gain.MaxGlobalGain;

    /// <summary>Un fos arriba al silenci quan l'amplitud és més petita que això (−60 dB).</summary>
    private const double SilenceAmplitude = 0.001;

    /// <summary>Estat que es pot desfer: els trossos que es queden i els canvis de volum (fosos).</summary>
    private sealed record EditState(IReadOnlyList<FrameRange> Segments, ImmutableDictionary<int, FrameGain> Gains);

    private readonly List<EditState> _undo = [];
    private readonly List<EditState> _redo = [];

    public Mp3Edit(Mp3FrameIndex index)
    {
        Index = index;
        Segments = [new FrameRange(0, index.FrameCount)];
        Gains = ImmutableDictionary<int, FrameGain>.Empty;
    }

    public Mp3FrameIndex Index { get; }

    /// <summary>Trossos de l'original que es queden, en ordre.</summary>
    public IReadOnlyList<FrameRange> Segments { get; private set; }

    /// <summary>
    /// L'original és un enregistrament en curs (fase 5, pas 24a; D-117): només creix pel final i els fotogrames ja
    /// escrits no canvien. En desar, n'hi ha prou que no s'hagi fet més curt.
    /// </summary>
    public bool Growing { get; init; }

    /// <summary>
    /// Canvis de volum dels fosos, per fotograma de l'<b>original</b>: si després es talla, el fos es mou
    /// amb el seu tros de so. Buit si no hi ha cap fos.
    /// </summary>
    public ImmutableDictionary<int, FrameGain> Gains { get; private set; }

    /// <summary>Grànuls per fotograma: 2 a MPEG-1 (1.152 mostres), 1 a MPEG-2 (576).</summary>
    public int GranulesPerFrame => Index.SamplesPerFrame >= 1152 ? 2 : 1;

    /// <summary>Fotogrames de la versió editada.</summary>
    public int FrameCount => Segments.Sum(s => s.Count);

    public TimeSpan Duration => TimeSpan.FromSeconds(FrameCount * Index.FrameSeconds);

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Hi ha canvis respecte del fitxer original.</summary>
    public bool IsModified => Segments.Count != 1 || Segments[0] != new FrameRange(0, Index.FrameCount) || !Gains.IsEmpty;

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
        Apply(Merge(result), Gains);
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
        Apply(Merge(Slice(Segments, start, end)), Gains);
        return true;
    }

    /// <summary>
    /// Fos d'entrada a [start, end) de la versió editada: del silenci al volum que tenia (amplitud lineal).
    /// Sense tornar a codificar: es canvia el volum global de cada grànul (D-073).
    /// </summary>
    /// <exception cref="Mp3EditException">Si no hi ha cap fragment seleccionat.</exception>
    public bool FadeIn(int start, int end) => Fade(start, end, fadeIn: true);

    /// <summary>Fos de sortida a [start, end) de la versió editada: del volum que tenia al silenci.</summary>
    /// <exception cref="Mp3EditException">Si no hi ha cap fragment seleccionat.</exception>
    public bool FadeOut(int start, int end) => Fade(start, end, fadeIn: false);

    /// <summary>
    /// Canvia el volum de [start, end) de la versió editada en <paramref name="steps"/> passos de 1,5 dB
    /// (positiu = més fort), a sobre dels fosos que hi hagi. Per a la normalització (fase 5, pas 26a; D-123).
    /// Sense tornar a codificar, com els fosos. Es pot desfer.
    /// </summary>
    /// <exception cref="Mp3EditException">Si el fragment és buit.</exception>
    public bool Gain(int start, int end, int steps)
    {
        (start, end) = Clamp(start, end);
        if (end <= start)
            throw new Mp3EditException("No hi ha res a normalitzar.");
        if (steps == 0) return false;

        bool twoGranules = GranulesPerFrame > 1;
        var gains = Gains.ToBuilder();
        foreach (var s in SegmentsBetween(start, end))
        {
            for (int original = s.Start; original < s.End; original++)
            {
                var current = gains.GetValueOrDefault(original);
                // Un grànul en silenci (fos fins al final) continua en silenci.
                int g0 = current.Granule0 <= SilenceSteps ? current.Granule0 : current.Granule0 + steps;
                int g1 = !twoGranules ? 0 : current.Granule1 <= SilenceSteps ? current.Granule1 : current.Granule1 + steps;
                if (g0 == 0 && g1 == 0) gains.Remove(original);
                else gains[original] = new FrameGain(g0, g1);
            }
        }
        Apply(Segments, gains.ToImmutable());
        return true;
    }

    private bool Fade(int start, int end, bool fadeIn)
    {
        (start, end) = Clamp(start, end);
        if (end <= start)
            throw new Mp3EditException("Cal seleccionar el fragment on es vol fer el fos.");

        int granules = GranulesPerFrame;
        int total = (end - start) * granules;
        var gains = Gains.ToBuilder();
        for (int i = start; i < end; i++)
        {
            int original = ToOriginal(i);
            var current = gains.GetValueOrDefault(original);
            int k = (i - start) * granules;
            int g0 = Combine(current.Granule0, StepsFor(k, total, fadeIn));
            int g1 = granules > 1 ? Combine(current.Granule1, StepsFor(k + 1, total, fadeIn)) : 0;
            if (g0 == 0 && g1 == 0) gains.Remove(original);
            else gains[original] = new FrameGain(g0, g1);
        }
        Apply(Segments, gains.ToImmutable());
        return true;
    }

    /// <summary>
    /// Passos del grànul <paramref name="k"/> de <paramref name="total"/> en un fos (amplitud lineal).
    /// El fos d'entrada comença en silenci (primer grànul) i el de sortida hi acaba (últim grànul).
    /// </summary>
    private static int StepsFor(int k, int total, bool fadeIn)
    {
        double amplitude = fadeIn ? (double)k / total : 1 - (double)(k + 1) / total;
        if (amplitude <= SilenceAmplitude) return SilenceSteps;
        return (int)Math.Round(20 * Math.Log10(amplitude) / Mp3Gain.StepDb);
    }

    /// <summary>Un fos a sobre d'un altre: se sumen (sense passar del silenci).</summary>
    private static int Combine(int current, int steps) => Math.Max(SilenceSteps, current + steps);

    /// <summary>
    /// Guany lineal d'un punt de l'original (en segons), segons els fosos: per a la previsualització.
    /// 1 = sense canvi; 0 = silenci.
    /// </summary>
    public float GainAt(double originalSeconds)
    {
        var gains = Gains;
        if (gains.IsEmpty || originalSeconds < 0) return 1f;
        int frame = (int)(originalSeconds / Index.FrameSeconds);
        if (!gains.TryGetValue(frame, out var gain)) return 1f;
        double within = originalSeconds - frame * Index.FrameSeconds;
        bool second = GranulesPerFrame > 1 && within >= Index.FrameSeconds / 2;
        return GainOfSteps(second ? gain.Granule1 : gain.Granule0);
    }

    /// <summary>Guany lineal d'uns quants passos (<see cref="SilenceSteps"/> o menys = 0).</summary>
    public static float GainOfSteps(int steps) =>
        steps <= SilenceSteps ? 0f : (float)Math.Pow(10, steps * Mp3Gain.StepDb / 20);

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Add(new EditState(Segments, Gains));
        (Segments, Gains) = (_undo[^1].Segments, _undo[^1].Gains);
        _undo.RemoveAt(_undo.Count - 1);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Add(new EditState(Segments, Gains));
        (Segments, Gains) = (_redo[^1].Segments, _redo[^1].Gains);
        _redo.RemoveAt(_redo.Count - 1);
        return true;
    }

    /// <summary>
    /// Desa la versió editada a <paramref name="target"/>: l'ID3v2, els fotogrames que es queden i l'ID3v1.
    /// S'escriu primer en un fitxer temporal a la mateixa carpeta, es força al disc i després ocupa el
    /// lloc del de destinació: si alguna cosa falla, el fitxer de destinació (o l'original) queda intacte.
    /// </summary>
    /// <param name="progress">Es crida de tant en tant amb la fracció feta (0..1).</param>
    /// <param name="only">Només aquests trossos de l'original (p. ex. els d'una selecció, <see cref="SegmentsBetween"/>;
    /// fase 5, pas 23d). Per defecte, tota la versió editada. L'edició no canvia.</param>
    /// <exception cref="Mp3EditException">Si l'original ha canviat, no es pot llegir o no es pot escriure la destinació.</exception>
    public void Save(string target, Action<double>? progress = null, CancellationToken cancel = default, IReadOnlyList<FrameRange>? only = null)
    {
        var source = new FileInfo(Index.Path);
        if (!source.Exists)
            throw new Mp3EditException("El fitxer original ja no hi és (l'han mogut o esborrat).");
        bool changed = Growing
            ? source.Length < Index.FileSize
            : source.Length != Index.FileSize || source.LastWriteTimeUtc != Index.LastWriteUtc;
        if (changed)
            throw new Mp3EditException("El fitxer original ha canviat des que s'ha obert a l'editor. Torna'l a obrir.");

        string folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(target))!;
        string temp = System.IO.Path.Combine(folder, $".{System.IO.Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        var segments = only ?? Segments;
        var gains = Gains;
        long totalFrames = Math.Max(1, segments.Sum(s => (long)s.Count)), done = 0;

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
                        if (HasGains(gains, f, last))
                        {
                            // Hi ha algun fos: fotograma a fotograma, canviant-ne el volum global (D-073).
                            for (int frame = f; frame <= last; frame++)
                                CopyFrame(input, output, frame, gains, buffer);
                        }
                        else
                        {
                            long from = Index.Offset(f);
                            long to = Index.Offset(last) + Index.Length(last);
                            Copy(input, output, from, to - from, buffer);
                        }
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

    private void Apply(IReadOnlyList<FrameRange> segments, ImmutableDictionary<int, FrameGain> gains)
    {
        _undo.Add(new EditState(Segments, Gains));
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
        Segments = segments;
        Gains = gains;
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

    private static bool HasGains(ImmutableDictionary<int, FrameGain> gains, int first, int last)
    {
        if (gains.IsEmpty) return false;
        for (int frame = first; frame <= last; frame++)
        {
            if (gains.ContainsKey(frame)) return true;
        }
        return false;
    }

    /// <summary>Copia un fotograma; si té fos, amb el volum global de cada grànul canviat.</summary>
    private void CopyFrame(Stream input, Stream output, int frame, ImmutableDictionary<int, FrameGain> gains, byte[] buffer)
    {
        int length = Index.Length(frame);
        input.Position = Index.Offset(frame);
        input.ReadExactly(buffer, 0, length);
        if (gains.TryGetValue(frame, out var gain))
        {
            var bytes = buffer.AsSpan(0, length);
            Mp3Gain.Adjust(bytes, 0, gain.Granule0);
            if (GranulesPerFrame > 1) Mp3Gain.Adjust(bytes, 1, gain.Granule1);
        }
        output.Write(buffer, 0, length);
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
