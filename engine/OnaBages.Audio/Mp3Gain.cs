namespace OnaBages.Audio;

/// <summary>
/// Volum d'un fotograma MP3 sense tornar a codificar (fase 4, pas 6a; D-073), com fa el mp3DirectCut.
///
/// Cada fotograma de capa III porta, per a cada «grànul» (576 mostres, uns 12 ms) i cada canal, un número
/// de volum global (<i>global_gain</i>, de 0 a 255). Sumar-hi 1 fa sonar aquell tros 1,5 dB més fort;
/// restar-n'hi 1, 1,5 dB més fluix. Si és 0, pràcticament silenci. Canviar-lo no toca res més del
/// fotograma (ni la mida ni les dades d'àudio), així que no es perd qualitat i el fitxer continua sent vàlid.
/// Si el fotograma porta codi de comprovació (CRC), es torna a calcular.
///
/// MPEG-1: 2 grànuls per fotograma. MPEG-2 i 2.5: 1. Mono: 1 canal; la resta: 2 (estèreo, conjunt o dual).
/// </summary>
public static class Mp3Gain
{
    /// <summary>Quants dB fa cada pas del volum global.</summary>
    public const double StepDb = 1.5;

    /// <summary>Volum global màxim (i mínim: 0 = silenci).</summary>
    public const int MaxGlobalGain = 255;

    private readonly record struct Layout(bool Mpeg1, int Channels, bool Crc)
    {
        public int Granules => Mpeg1 ? 2 : 1;
        public int SideInfoBytes => Mpeg1 ? (Channels == 1 ? 17 : 32) : (Channels == 1 ? 9 : 17);
        public int SideInfoStart => Crc ? 6 : 4;

        /// <summary>Posició (en bits, des de l'inici de la informació lateral) del volum global d'un grànul i canal.</summary>
        public int GlobalGainBit(int granule, int channel)
        {
            int start = Mpeg1 ? 9 + (Channels == 1 ? 5 : 3) + 4 * Channels : 8 + (Channels == 1 ? 1 : 2);
            int blockBits = Mpeg1 ? 59 : 63;
            return start + (granule * Channels + channel) * blockBits + 12 + 9; // després de part2_3_length i big_values
        }
    }

    /// <summary>Com és el fotograma (null si no és de capa III o és massa curt).</summary>
    private static Layout? LayoutOf(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 6 || frame[0] != 0xFF || (frame[1] & 0xE0) != 0xE0) return null;
        int version = (frame[1] >> 3) & 0x03;
        int layer = (frame[1] >> 1) & 0x03;
        if (version == 1 || layer != 1) return null;
        bool crc = (frame[1] & 0x01) == 0;
        int channels = ((frame[3] >> 6) & 0x03) == 3 ? 1 : 2;
        var layout = new Layout(version == 3, channels, crc);
        return frame.Length >= layout.SideInfoStart + layout.SideInfoBytes ? layout : null;
    }

    /// <summary>Grànuls del fotograma (2 a MPEG-1, 1 a MPEG-2), o 0 si no és un fotograma de capa III.</summary>
    public static int GranuleCount(ReadOnlySpan<byte> frame) => LayoutOf(frame)?.Granules ?? 0;

    /// <summary>Canals del fotograma (1 o 2), o 0 si no és un fotograma de capa III.</summary>
    public static int ChannelCount(ReadOnlySpan<byte> frame) => LayoutOf(frame)?.Channels ?? 0;

    /// <summary>Volum global d'un grànul i canal (0..255), o −1 si no és un fotograma de capa III.</summary>
    public static int Read(ReadOnlySpan<byte> frame, int granule, int channel)
    {
        if (LayoutOf(frame) is not { } layout || granule >= layout.Granules || channel >= layout.Channels) return -1;
        return ReadBits(frame[layout.SideInfoStart..], layout.GlobalGainBit(granule, channel), 8);
    }

    /// <summary>
    /// Canvia el volum d'un grànul (tots els canals) en <paramref name="steps"/> passos de 1,5 dB
    /// (negatiu = més fluix). Sense límit cap avall: <see cref="int.MinValue"/> deixa el grànul en silenci.
    /// Retorna false si el fotograma no és de capa III (llavors no es toca).
    /// </summary>
    public static bool Adjust(Span<byte> frame, int granule, int steps)
    {
        if (LayoutOf(frame) is not { } layout || granule >= layout.Granules) return false;
        var side = frame.Slice(layout.SideInfoStart, layout.SideInfoBytes);
        for (int channel = 0; channel < layout.Channels; channel++)
        {
            int bit = layout.GlobalGainBit(granule, channel);
            long value = (long)ReadBits(side, bit, 8) + steps;
            WriteBits(side, bit, 8, (int)Math.Clamp(value, 0, MaxGlobalGain));
        }
        if (layout.Crc) WriteCrc(frame, layout);
        return true;
    }

    /// <summary>El CRC del fotograma és correcte (o no en porta). Per a les proves i per comprovar fitxers.</summary>
    public static bool CrcIsValid(ReadOnlySpan<byte> frame)
    {
        if (LayoutOf(frame) is not { } layout) return false;
        if (!layout.Crc) return true;
        return Crc(frame, layout) == (frame[4] << 8 | frame[5]);
    }

    /// <summary>
    /// CRC-16 de l'MPEG: polinomi 0x8005, valor inicial 0xFFFF, sobre els dos últims bytes de la capçalera
    /// i la informació lateral.
    /// </summary>
    public static ushort Crc16(ReadOnlySpan<byte> data, ushort crc = 0xFFFF)
    {
        foreach (byte b in data)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                bool top = (((crc >> 15) ^ (b >> bit)) & 1) != 0;
                crc = (ushort)(crc << 1);
                if (top) crc ^= 0x8005;
            }
        }
        return crc;
    }

    private static ushort Crc(ReadOnlySpan<byte> frame, Layout layout) =>
        Crc16(frame.Slice(layout.SideInfoStart, layout.SideInfoBytes), Crc16(frame.Slice(2, 2)));

    private static void WriteCrc(Span<byte> frame, Layout layout)
    {
        ushort crc = Crc(frame, layout);
        frame[4] = (byte)(crc >> 8);
        frame[5] = (byte)crc;
    }

    // Bits en ordre de lectura de l'MPEG: el més significatiu de cada byte primer.

    private static int ReadBits(ReadOnlySpan<byte> data, int bitOffset, int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++)
        {
            int bit = bitOffset + i;
            value = (value << 1) | ((data[bit >> 3] >> (7 - (bit & 7))) & 1);
        }
        return value;
    }

    private static void WriteBits(Span<byte> data, int bitOffset, int count, int value)
    {
        for (int i = 0; i < count; i++)
        {
            int bit = bitOffset + i;
            int mask = 1 << (7 - (bit & 7));
            if (((value >> (count - 1 - i)) & 1) != 0) data[bit >> 3] |= (byte)mask;
            else data[bit >> 3] &= (byte)~mask;
        }
    }
}
