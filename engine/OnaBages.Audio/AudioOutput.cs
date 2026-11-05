using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace OnaBages.Audio;

/// <summary>
/// Una sortida d'àudio cap a un dispositiu. N'hi ha una de real (<see cref="WasapiAudioOutput"/>)
/// i les proves en fan servir una de falsa, per no dependre de cap targeta de so.
/// </summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>Freqüència del dispositiu (mode compartit de Windows).</summary>
    int SampleRate { get; }

    /// <summary>Canals del dispositiu (normalment 2).</summary>
    int Channels { get; }

    /// <summary>Connecta la font de mostres. La sortida hi llegirà des del seu fil d'àudio.</summary>
    void Init(IWaveProvider source);

    void Play();
    void Pause();
    void Stop();

    /// <summary>
    /// La sortida s'ha aturat. Si porta una excepció, és un error (p. ex. el dispositiu
    /// s'ha desconnectat). Qui escolta només ha d'apuntar-ho: no pot tancar la sortida aquí dins.
    /// </summary>
    event Action<Exception?>? Stopped;
}

/// <summary>
/// Sortida real per WASAPI en mode compartit, cap a un dispositiu concret (mai el predeterminat).
/// El fil d'àudio de Windows té prioritat «Pro Audio» (MMCSS).
/// </summary>
public sealed class WasapiAudioOutput : IAudioOutput
{
    /// <summary>Memòria de la sortida de Windows, en mil·lisegons.</summary>
    public const int LatencyMs = 100;

    private readonly MMDevice _device;
    private readonly WasapiPlayer _out;

    private WasapiAudioOutput(MMDevice device)
    {
        _device = device;
        _out = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(LatencyMs)
            .WithMmcssThreadPriority("Pro Audio")
            .Build();
        _out.PlaybackStopped += (_, e) => Stopped?.Invoke(e.Exception);

        // Format del mescladiu de Windows: hi enviem exactament aquesta freqüència i canals.
        SampleRate = _out.DeviceMixFormat.SampleRate;
        Channels = _out.DeviceMixFormat.Channels;
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public event Action<Exception?>? Stopped;

    /// <summary>Obre el dispositiu amb aquest identificador de Windows.</summary>
    /// <exception cref="AudioDeviceException">Si el dispositiu no hi és o no està actiu.</exception>
    public static WasapiAudioOutput Open(string deviceId)
    {
        MMDevice device;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDevice(deviceId);
        }
        catch (Exception ex)
        {
            throw new AudioDeviceException("Dispositiu no disponible: no es troba a Windows.", ex);
        }

        try
        {
            if (device.State != DeviceState.Active)
                throw new AudioDeviceException($"Dispositiu no disponible: {device.FriendlyName} no està actiu.");

            return new WasapiAudioOutput(device);
        }
        catch (AudioDeviceException)
        {
            device.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            string name;
            try { name = device.FriendlyName; } catch { name = "de sortida"; } // el dispositiu pot estar malmès
            device.Dispose();
            throw new AudioDeviceException($"No s'ha pogut obrir el dispositiu {name}.", ex);
        }
    }

    /// <summary>
    /// Amb més de 2 canals (p. ex. una sortida configurada com a 5.1 o 7.1), Windows no accepta
    /// el format senzill: cal el format «extensible» amb la mateixa disposició d'altaveus que el
    /// mescladiu (si no, l'error és 0x80070057). Llavors s'hi declara exactament el del dispositiu,
    /// que en mode compartit sempre és float de 32 bits, com el que envia el reproductor.
    /// </summary>
    public void Init(IWaveProvider source)
    {
        var mix = _out.DeviceMixFormat;
        if (Channels > 2 && mix.BitsPerSample == 32)
            source = new FormatOverride(source, mix);
        _out.Init(source);
    }

    /// <summary>La mateixa font, però declarant el format exacte del dispositiu.</summary>
    private sealed class FormatOverride(IWaveProvider source, WaveFormat format) : IWaveProvider
    {
        public WaveFormat WaveFormat => format;
        public int Read(Span<byte> buffer) => source.Read(buffer);
    }

    public void Play() => _out.Play();
    public void Pause() => _out.Pause();
    public void Stop() => _out.Stop();

    public void Dispose()
    {
        _out.Dispose();
        _device.Dispose();
    }
}

/// <summary>Error en obrir o fer servir un dispositiu d'àudio. El missatge és per a l'operador.</summary>
public sealed class AudioDeviceException(string message, Exception? inner = null) : Exception(message, inner);
