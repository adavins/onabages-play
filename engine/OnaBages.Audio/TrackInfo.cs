using System.Globalization;

namespace OnaBages.Audio;

/// <summary>Resultat de comprovar una pista: durada i format (p. ex. «MP3 192 kbps estèreo 44,1 kHz»).</summary>
public sealed record TrackProbe(TimeSpan Duration, string Format);

/// <summary>Metadades d'un fitxer, per a la finestra d'informació. Els camps que no hi són, null.</summary>
public sealed record TrackMetadata(
    string Path,
    long FileSize,
    TimeSpan Duration,
    string Format,
    string? Title,
    string? Artist,
    string? Album,
    uint? Year,
    string? Genre,
    string? Comment);

/// <summary>
/// Comprovació i informació de pistes. No fa servir el fil d'àudio: es crida des d'un fil de fons.
/// </summary>
public static class TrackInfo
{
    /// <summary>
    /// Comprova que la pista es pot reproduir de debò: l'obre amb el mateix descodificador que el
    /// reproductor i en llegeix el primer mig segon. Si no llança cap error, la pista sonarà.
    /// </summary>
    /// <exception cref="AudioFileException">Si no existeix, és malmesa o té un format no compatible.</exception>
    public static TrackProbe Probe(string path)
    {
        using var source = AudioFileSource.Open(path, 48000);
        var buffer = new float[48000]; // 0,5 s en estèreo
        try
        {
            source.Read(buffer);
        }
        catch (Exception ex)
        {
            throw new AudioFileException($"El fitxer {System.IO.Path.GetFileName(path)} és malmès: no es pot descodificar.", ex);
        }

        if (source.Duration <= TimeSpan.Zero)
            throw new AudioFileException($"El fitxer {System.IO.Path.GetFileName(path)} no té àudio.");

        return new TrackProbe(source.Duration, Describe(path, source.SourceSampleRate, source.SourceChannels));
    }

    /// <summary>Metadades (títol, artista...) amb TagLib#. Si no se'n poden llegir, només el que se sap del fitxer.</summary>
    public static TrackMetadata ReadMetadata(string path)
    {
        var probe = Probe(path);
        long size = new FileInfo(path).Length;
        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;
            return new TrackMetadata(
                path, size, probe.Duration, probe.Format,
                Empty(tag.Title),
                Empty(tag.JoinedPerformers),
                Empty(tag.Album),
                tag.Year > 0 ? tag.Year : null,
                Empty(tag.JoinedGenres),
                Empty(tag.Comment));
        }
        catch (Exception)
        {
            // Format que TagLib# no coneix, o etiquetes malmeses: sense metadades, però el fitxer sona.
            return new TrackMetadata(path, size, probe.Duration, probe.Format, null, null, null, null, null, null);
        }
    }

    /// <summary>
    /// Durada sense descodificar l'àudio (fase 5, pas 15a; per a l'explorador del panell lateral): la diu TagLib#
    /// llegint les capçaleres (en un MP3 de bitrate variable, la capçalera Xing). És aproximada en alguns fitxers,
    /// prou bona per mostrar-la i ordenar. Mai no llança excepcions: si no es pot llegir, null.
    /// </summary>
    public static TimeSpan? QuickDuration(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path, TagLib.ReadStyle.Average);
            var duration = file.Properties?.Duration ?? TimeSpan.Zero;
            return duration > TimeSpan.Zero ? duration : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Només el títol i l'artista (per a l'historial), sense descodificar l'àudio.
    /// Mai no llança excepcions: si no es poden llegir, null.
    /// </summary>
    public static (string? Title, string? Artist) ReadTags(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            return (Empty(file.Tag.Title), Empty(file.Tag.JoinedPerformers));
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Format abreujat. El tipus i els kbps els diu TagLib# (llegint la capçalera, sense descodificar);
    /// si no ho sap, es fa servir el que diu el descodificador.
    /// </summary>
    private static string Describe(string path, int sampleRate, int channels)
    {
        string codec = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant() switch
        {
            "M4A" or "MP4" => "AAC",
            "" => "?",
            var ext => ext,
        };

        int bitrate = 0, bits = 0;
        try
        {
            using var file = TagLib.File.Create(path);
            var props = file.Properties;
            bitrate = props.AudioBitrate;
            bits = props.BitsPerSample;
            if (props.AudioSampleRate > 0) sampleRate = props.AudioSampleRate;
            if (props.AudioChannels > 0) channels = props.AudioChannels;
        }
        catch (Exception)
        {
            // Sense TagLib#: només freqüència i canals.
        }

        var parts = new List<string> { codec };
        bool lossless = codec is "WAV" or "FLAC" or "AIFF";
        if (lossless && bits > 0) parts.Add($"{bits} bits");
        else if (bitrate > 0) parts.Add($"{bitrate} kbps");
        parts.Add(channels switch { 1 => "mono", 2 => "estèreo", _ => $"{channels} canals" });
        parts.Add($"{(sampleRate / 1000.0).ToString("0.#", CultureInfo.GetCultureInfo("ca-ES"))} kHz");
        return string.Join(' ', parts);
    }

    private static string? Empty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
