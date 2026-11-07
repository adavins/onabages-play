namespace OnaBages.Audio;

/// <summary>
/// Forma d'ona per a l'editor (especificació 9.3): el pic de cada fotograma MP3 i de cada canal,
/// de 0 (silenci) a 255 (escala completa), lineal. Dos bytes per fotograma: esquerre i dret.
/// Una hora a 48 kHz són uns 150.000 fotogrames: 300 KB.
///
/// Es calcula descodificant el fitxer sencer (uns segons per hora): s'ha de cridar des d'un
/// fil de fons de prioritat baixa, mai des del fil d'àudio.
/// </summary>
public static class WaveformPeaks
{
    public const int BytesPerFrame = 2;

    /// <summary>
    /// Calcula els pics de <paramref name="frameCount"/> fotogrames i els escriu a <paramref name="target"/>
    /// (que ha de tenir <c>frameCount * 2</c> bytes) a mesura que avança.
    /// </summary>
    /// <param name="progress">Es crida de tant en tant amb quants fotogrames ja estan fets.</param>
    /// <exception cref="AudioFileException">Si el fitxer no es pot llegir.</exception>
    public static void Compute(string path, int sampleRate, int samplesPerFrame, int frameCount, byte[] target,
        Action<int>? progress = null, CancellationToken cancel = default)
    {
        if (target.Length < frameCount * BytesPerFrame)
            throw new ArgumentException("La memòria de destinació és massa petita.", nameof(target));

        // A la freqüència original: sense remostrejar, cada bloc de mostres és un fotograma.
        using var source = AudioFileSource.Open(path, sampleRate);
        const int FramesPerRead = 64;
        var buffer = new float[samplesPerFrame * 2 * FramesPerRead];
        int frame = 0, inFrame = 0;
        float left = 0, right = 0;

        while (frame < frameCount)
        {
            cancel.ThrowIfCancellationRequested();
            int read;
            try { read = source.Read(buffer); }
            catch (Exception ex)
            {
                throw new AudioFileException($"No s'ha pogut descodificar {System.IO.Path.GetFileName(path)} per fer-ne la forma d'ona.", ex);
            }
            if (read == 0) break;

            for (int i = 0; i + 1 < read; i += 2)
            {
                float l = Math.Abs(buffer[i]), r = Math.Abs(buffer[i + 1]);
                if (l > left) left = l;
                if (r > right) right = r;
                if (++inFrame == samplesPerFrame)
                {
                    Store(target, frame, left, right);
                    frame++;
                    inFrame = 0;
                    left = right = 0;
                    if (frame == frameCount) break;
                }
            }
            progress?.Invoke(frame);
        }

        // L'últim fotograma a mitges (o els que el descodificador no ha donat): el que hi hagi.
        if (frame < frameCount && inFrame > 0)
            Store(target, frame++, left, right);
        progress?.Invoke(frameCount);
    }

    /// <summary>
    /// Forma d'ona d'una versió editada a partir de la de l'original: els trossos que es queden,
    /// un darrere l'altre. Serveix per no haver de tornar a descodificar un fitxer acabat de desar.
    /// </summary>
    /// <param name="gains">Fosos (fase 4, pas 6b): els pics d'aquests fotogrames es multipliquen pel guany més alt dels seus grànuls.</param>
    public static byte[] Splice(byte[] original, IReadOnlyList<FrameRange> segments, IReadOnlyDictionary<int, FrameGain>? gains = null)
    {
        var result = new byte[segments.Sum(s => s.Count) * BytesPerFrame];
        int pos = 0;
        foreach (var s in segments)
        {
            Array.Copy(original, s.Start * BytesPerFrame, result, pos, s.Count * BytesPerFrame);
            if (gains is { Count: > 0 })
            {
                for (int f = 0; f < s.Count; f++)
                {
                    if (!gains.TryGetValue(s.Start + f, out var gain)) continue;
                    float factor = Mp3Edit.GainOfSteps(Math.Max(gain.Granule0, gain.Granule1));
                    int at = pos + f * BytesPerFrame;
                    result[at] = (byte)Math.Min(255f, MathF.Round(result[at] * factor)); // amb la normalització, el guany pot ser > 1
                    result[at + 1] = (byte)Math.Min(255f, MathF.Round(result[at + 1] * factor));
                }
            }
            pos += s.Count * BytesPerFrame;
        }
        return result;
    }

    private static void Store(byte[] target, int frame, float left, float right)
    {
        target[frame * 2] = ToByte(left);
        target[frame * 2 + 1] = ToByte(right);
    }

    private static byte ToByte(float peak) => (byte)Math.Clamp((int)MathF.Round(peak * 255), 0, 255);
}
