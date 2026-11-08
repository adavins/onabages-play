using System.Runtime.InteropServices;

namespace OnaBages.Audio;

/// <summary>
/// Codificador MP3 amb LAME (libmp3lame.dll, LGPL, enllaç dinàmic).
///
/// Configuració fixa de l'especificació (8.4):
/// - CBR (bitrate constant), 320 kbps per defecte, estèreo;
/// - <b>sense etiqueta Xing/Info</b>: el fitxer és vàlid en tot moment, fins i tot si es talla;
/// - <b>sense «bit reservoir»</b>: cada fotograma es pot descodificar sol. Així els talls de
///   l'editor (fase 3) i la recuperació d'un fitxer interromput són nets. A 320 kbps la
///   diferència de qualitat és inapreciable.
///
/// No és segur entre fils: només l'ha de fer servir el fil de codificació.
/// </summary>
public sealed class LameEncoder : IDisposable
{
    private const string Lib = "libmp3lame";

    private nint _lame;
    private byte[] _mp3Buffer = [];

    /// <param name="sampleRate">Freqüència de les mostres d'entrada.</param>
    /// <param name="bitrateKbps">Bitrate constant. Per defecte, 320.</param>
    /// <param name="mono">MP3 mono (l'entrada continua sent estèreo; LAME la barreja). Per a les proves i l'streaming.</param>
    /// <param name="outputSampleRate">Freqüència del MP3. Per defecte, la de l'entrada (no es remostreja); si és diferent,
    /// LAME la converteix (streaming: una targeta a 48 kHz que emet a 44,1 kHz).</param>
    /// <param name="crc">Amb codi de comprovació (CRC) a cada fotograma. Per a les proves.</param>
    /// <exception cref="AudioFileException">Si LAME no es pot carregar o no accepta la configuració.</exception>
    public LameEncoder(int sampleRate, int bitrateKbps = 320, bool mono = false, bool crc = false, int? outputSampleRate = null)
    {
        SampleRate = sampleRate;
        BitrateKbps = bitrateKbps;

        try
        {
            _lame = lame_init();
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            throw new AudioFileException("No es pot carregar el codificador MP3 (libmp3lame.dll). Cal reinstal·lar l'aplicació.", ex);
        }
        if (_lame == 0)
            throw new AudioFileException("No s'ha pogut iniciar el codificador MP3.");

        lame_set_in_samplerate(_lame, sampleRate);
        lame_set_out_samplerate(_lame, outputSampleRate ?? sampleRate);
        lame_set_num_channels(_lame, 2);
        lame_set_mode(_lame, mono ? Mono : JointStereo);
        if (crc) lame_set_error_protection(_lame, 1);
        lame_set_VBR(_lame, VbrOff);
        lame_set_brate(_lame, bitrateKbps);
        lame_set_quality(_lame, 2);              // alta qualitat (com «-q 2»); prou ràpid per a diversos enregistradors
        lame_set_bWriteVbrTag(_lame, 0);         // sense etiqueta Xing/Info
        lame_set_disable_reservoir(_lame, 1);    // fotogrames independents

        if (lame_init_params(_lame) < 0)
        {
            lame_close(_lame);
            _lame = 0;
            throw new AudioFileException($"El codificador MP3 no accepta {outputSampleRate ?? sampleRate} Hz a {bitrateKbps} kbps.");
        }
    }

    public int SampleRate { get; }
    public int BitrateKbps { get; }

    /// <summary>Versió de LAME carregada (p. ex. "3.100").</summary>
    public static string Version => Marshal.PtrToStringAnsi(get_lame_version()) ?? "?";

    /// <summary>
    /// Codifica mostres estèreo intercalades (float, -1..1) i retorna els bytes MP3 produïts
    /// (pot ser buit: LAME acumula fins a tenir fotogrames sencers).
    /// El resultat és vàlid fins a la crida següent.
    /// </summary>
    public ReadOnlySpan<byte> Encode(float[] stereoSamples, int count)
    {
        int frames = count / 2;
        EnsureBuffer(frames);
        int bytes = lame_encode_buffer_interleaved_ieee_float(_lame, stereoSamples, frames, _mp3Buffer, _mp3Buffer.Length);
        if (bytes < 0)
            throw new AudioFileException($"Error del codificador MP3 (codi {bytes}).");
        return _mp3Buffer.AsSpan(0, bytes);
    }

    /// <summary>Acaba la codificació i retorna els últims fotogrames. Només s'ha de cridar un cop, al final.</summary>
    public ReadOnlySpan<byte> Flush()
    {
        EnsureBuffer(0);
        int bytes = lame_encode_flush(_lame, _mp3Buffer, _mp3Buffer.Length);
        return bytes > 0 ? _mp3Buffer.AsSpan(0, bytes) : [];
    }

    public void Dispose()
    {
        if (_lame != 0)
        {
            lame_close(_lame);
            _lame = 0;
        }
    }

    /// <summary>Mida recomanada per LAME: 1,25 × mostres + 7200 bytes.</summary>
    private void EnsureBuffer(int frames)
    {
        int needed = frames + frames / 4 + 7200;
        if (_mp3Buffer.Length < needed) _mp3Buffer = new byte[needed];
    }

    // -----------------------------------------------------------------------
    // Funcions de libmp3lame.dll (lame.h)
    // -----------------------------------------------------------------------

    private const int JointStereo = 1; // MPEG_mode.JOINT_STEREO
    private const int Mono = 3;        // MPEG_mode.MONO
    private const int VbrOff = 0;      // vbr_mode.vbr_off (CBR)

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern nint lame_init();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int lame_close(nint gfp);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int lame_init_params(nint gfp);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int lame_set_in_samplerate(nint gfp, int value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int lame_set_out_samplerate(nint gfp, int value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int lame_set_num_channels(nint gfp, int value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int lame_set_mode(nint gfp, int value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int lame_set_error_protection(nint gfp, int value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int lame_set_VBR(nint gfp, int value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int lame_set_brate(nint gfp, int value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int lame_set_quality(nint gfp, int value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int lame_set_bWriteVbrTag(nint gfp, int value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int lame_set_disable_reservoir(nint gfp, int value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern nint get_lame_version();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lame_encode_buffer_interleaved_ieee_float(nint gfp, float[] pcm, int frames, byte[] mp3buf, int mp3bufSize);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lame_encode_flush(nint gfp, byte[] mp3buf, int size);
}
