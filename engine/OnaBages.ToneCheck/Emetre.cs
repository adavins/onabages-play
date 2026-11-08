using System.Diagnostics;
using OnaBages.Audio;

namespace OnaBages.ToneCheck;

/// <summary>
/// Prova de l'emissor d'streaming amb un servidor de debò (fase 5, pas 31b): envia un to de 440 Hz en temps real
/// durant uns segons, amb un títol, i cada segon diu com va. Serveix per provar la connexió abans de tenir la pantalla.
/// La contrasenya es demana per teclat (no queda a l'historial de la consola).
/// </summary>
internal static class Emetre
{
    public static int Run(StreamServerType type, string host, int port, string mountOrSid, int seconds, int bitrate, int sampleRate)
    {
        Console.Write("Contrasenya de la font: ");
        string password = ReadPassword();

        var server = type == StreamServerType.Icecast
            ? new StreamServer { Type = type, Host = host, Port = port, Password = password, Mount = mountOrSid }
            : new StreamServer { Type = type, Host = host, Port = port, Password = password, StreamId = int.TryParse(mountOrSid, out int sid) ? sid : 1 };

        using var input = new ToneInput(48000);
        using var streamer = new Streamer("prova", _ => input);
        streamer.SetDevice("to");
        streamer.SetServer(server, new StreamInfo { Name = "Ona Bages Play (prova)", Genre = "Prova" });
        streamer.SetQuality(bitrate, sampleRate, mono: false);
        streamer.SetTitle("Prova d'Ona Bages Play: to de 440 Hz");

        Console.WriteLine($"Emetent {seconds} s de to cap a {host}:{server.SourcePort} ({type}, {bitrate} kbps, {sampleRate} Hz)...");
        streamer.Start();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds && streamer.State != StreamerState.Error)
        {
            Thread.Sleep(1000);
            streamer.Tick();
            Console.WriteLine($"{clock.Elapsed:mm\\:ss}  {StateName(streamer.State),-14}  enviat {streamer.BytesSent / 1024,6} kB" +
                $"  reconnexions {streamer.Reconnects}" +
                (streamer.ConnectionError is { } e ? $"  · {e}" : "") +
                (streamer.TitleError is { } t ? $"  · títol: {t}" : ""));
        }

        bool ok = streamer.State == StreamerState.Streaming && streamer.BytesSent > 0;
        if (streamer.State == StreamerState.Error) Console.WriteLine($"Error: {streamer.ErrorMessage} ({streamer.ErrorDetail})");
        else streamer.Stop();
        Console.WriteLine(ok ? "Fet: l'emissió ha funcionat." : "No s'ha pogut emetre.");
        return ok ? 0 : 1;
    }

    private static string StateName(StreamerState state) => state switch
    {
        StreamerState.Connecting => "Connectant",
        StreamerState.Streaming => "Emetent",
        StreamerState.Reconnecting => "Reconnectant",
        StreamerState.Error => "Error",
        _ => "Aturat",
    };

    private static string ReadPassword()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
        }
        Console.WriteLine();
        return text.ToString();
    }

    /// <summary>Entrada falsa que dona un to de 440 Hz (−12 dBFS) en temps real, en paquets de 10 ms.</summary>
    private sealed class ToneInput(int sampleRate) : IAudioInput
    {
        private Thread? _thread;
        private volatile bool _running;

        public int SampleRate => sampleRate;
        public int Channels => 2;
        public event SamplesCapturedHandler? DataAvailable;
#pragma warning disable CS0067 // el to no es desconnecta mai
        public event Action<Exception?>? Stopped;
#pragma warning restore CS0067

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "To de prova" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            _thread?.Join();
        }

        public void Dispose() => Stop();

        private void Run()
        {
            int packet = sampleRate / 100;
            var buffer = new float[packet * 2];
            long frame = 0;
            var clock = Stopwatch.StartNew();
            while (_running)
            {
                // Tants paquets com toquen pel temps que ha passat (com una targeta de debò).
                while (frame < clock.Elapsed.TotalSeconds * sampleRate)
                {
                    for (int i = 0; i < packet; i++)
                    {
                        float v = 0.25f * (float)Math.Sin(2 * Math.PI * 440 * (frame + i) / sampleRate);
                        buffer[i * 2] = v;
                        buffer[i * 2 + 1] = v;
                    }
                    DataAvailable?.Invoke(buffer, false, false);
                    frame += packet;
                }
                Thread.Sleep(5);
            }
        }
    }
}
