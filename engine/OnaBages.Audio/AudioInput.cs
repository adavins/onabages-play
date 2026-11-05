using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace OnaBages.Audio;

/// <summary>
/// Mostres capturades (float intercalades, amb els canals de l'entrada).
/// <paramref name="samples"/> només és vàlid durant la crida.
/// </summary>
/// <param name="silent">Windows indica que el paquet s'ha de tractar com a silenci.</param>
/// <param name="discontinuity">Windows indica que s'han perdut dades abans d'aquest paquet.</param>
public delegate void SamplesCapturedHandler(ReadOnlySpan<float> samples, bool silent, bool discontinuity);

/// <summary>
/// Una entrada d'àudio. N'hi ha una de real (<see cref="WasapiAudioInput"/>) i una de falsa per a les proves.
/// </summary>
public interface IAudioInput : IDisposable
{
    /// <summary>Freqüència de l'entrada (mode compartit de Windows).</summary>
    int SampleRate { get; }

    /// <summary>Canals de l'entrada (1, 2 o més).</summary>
    int Channels { get; }

    void Start();

    /// <summary>Atura la captura. Quan retorna, ja no arribaran més mostres.</summary>
    void Stop();

    /// <summary>Crida des del fil de captura: només s'hi pot copiar les mostres i comptar.</summary>
    event SamplesCapturedHandler? DataAvailable;

    /// <summary>La captura s'ha aturat. Si porta una excepció, és un error (p. ex. entrada desconnectada).</summary>
    event Action<Exception?>? Stopped;
}

/// <summary>Entrada real per WASAPI en mode compartit, d'un dispositiu concret, amb prioritat «Pro Audio».</summary>
public sealed class WasapiAudioInput : IAudioInput
{
    /// <summary>Memòria de captura de Windows, en mil·lisegons.</summary>
    public const int BufferMs = 100;

    private readonly MMDevice _device;
    private readonly WasapiRecorder _recorder;
    private readonly ManualResetEventSlim _stopped = new(true);

    private WasapiAudioInput(MMDevice device, WaveFormat mix)
    {
        _device = device;
        SampleRate = mix.SampleRate;
        Channels = mix.Channels;

        // Sempre float a la freqüència i canals del dispositiu: Windows només canvia el format de mostra.
        // Amb més de 2 canals cal el format «extensible» del dispositiu (si no, error 0x80070057);
        // en mode compartit ja és float de 32 bits.
        var format = Channels > 2 && mix.BitsPerSample == 32
            ? mix
            : WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

        _recorder = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithBufferLength(BufferMs)
            .WithFormat(format)
            .WithMmcssThreadPriority("Pro Audio")
            .Build();

        _recorder.DataAvailable += OnData;
        _recorder.RecordingStopped += (_, e) =>
        {
            _stopped.Set();
            Stopped?.Invoke(e.Exception);
        };
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public event SamplesCapturedHandler? DataAvailable;
    public event Action<Exception?>? Stopped;

    /// <summary>Obre l'entrada amb aquest identificador de Windows.</summary>
    /// <exception cref="AudioDeviceException">Si l'entrada no hi és, no està activa o no és una entrada.</exception>
    public static WasapiAudioInput Open(string deviceId)
    {
        MMDevice device;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDevice(deviceId);
        }
        catch (Exception ex)
        {
            throw new AudioDeviceException("Entrada no disponible: no es troba a Windows.", ex);
        }

        try
        {
            if (device.DataFlow != DataFlow.Capture)
                throw new AudioDeviceException($"{device.FriendlyName} no és una entrada.");
            if (device.State != DeviceState.Active)
                throw new AudioDeviceException($"Entrada no disponible: {device.FriendlyName} no està activa.");

            WaveFormat mix;
            using (var client = device.CreateAudioClient())
                mix = client.MixFormat;

            return new WasapiAudioInput(device, mix);
        }
        catch (AudioDeviceException)
        {
            device.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            string name;
            try { name = device.FriendlyName; } catch { name = "d'entrada"; }
            device.Dispose();
            throw new AudioDeviceException($"No s'ha pogut obrir el dispositiu {name}.", ex);
        }
    }

    // Fil de captura de Windows: no crea objectes, només passa la vista de les mostres.
    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        DataAvailable?.Invoke(
            MemoryMarshal.Cast<byte, float>(buffer),
            (flags & AudioClientBufferFlags.Silent) != 0,
            (flags & AudioClientBufferFlags.DataDiscontinuity) != 0);
    }

    public void Start()
    {
        _stopped.Reset();
        _recorder.StartRecording();
    }

    public void Stop()
    {
        _recorder.StopRecording();
        _stopped.Wait(TimeSpan.FromSeconds(2)); // espera que el fil de captura hagi acabat
    }

    public void Dispose()
    {
        _recorder.Dispose();
        _device.Dispose();
        _stopped.Dispose();
    }
}
