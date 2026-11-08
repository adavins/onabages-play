using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OnaBages.Audio;

namespace OnaBages.Tests;

/// <summary>
/// Emissor d'streaming (fase 5, pas 31b; D-140). Una entrada falsa fa de targeta i un servidor Icecast fals rep l'MP3:
/// es comprova que arriba sencer (durada, freqüència, bitrate), que es torna a connectar sol i que el títol s'envia.
/// </summary>
public class StreamerTests
{
    private const int Rate = 48000;

    private static Streamer Create(FakeInput input, FakeIcecast server, List<string>? titles = null)
    {
        var streamer = new Streamer("1", _ => input, (_, title) => { if (titles is null) return; lock (titles) titles.Add(title); })
        {
            RetryDelay = TimeSpan.FromMilliseconds(200),
        };
        streamer.SetDevice("entrada");
        streamer.SetServer(new StreamServer { Type = StreamServerType.Icecast, Host = "127.0.0.1", Port = server.Port, Password = "x" },
            new StreamInfo { Name = "Ona Bages" });
        return streamer;
    }

    /// <summary>Crida <see cref="Streamer.Tick"/> fins que es compleixi la condició (com fa el motor).</summary>
    private static void WaitFor(Streamer streamer, Func<bool> condition, int timeoutMs = 10_000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            streamer.Tick();
            if (condition()) return;
            if (DateTime.UtcNow > until) throw new TimeoutException($"No ha arribat a passar (estat: {streamer.State}, {streamer.ConnectionError}).");
            Thread.Sleep(20);
        }
    }

    [Fact]
    public void StartNeedsSourceAndServer()
    {
        var streamer = new Streamer("1", _ => new FakeInput());
        var ex = Assert.Throws<StreamerCommandException>(streamer.Start);
        Assert.Contains("font", ex.Message);

        streamer.SetDevice("entrada");
        ex = Assert.Throws<StreamerCommandException>(streamer.Start);
        Assert.Contains("servidor", ex.Message);
    }

    [Fact]
    public void SettingsAreChecked()
    {
        var streamer = new Streamer("1", _ => new FakeInput());
        Assert.Throws<StreamerCommandException>(() => streamer.SetQuality(100, 44100, false));
        Assert.Throws<StreamerCommandException>(() => streamer.SetQuality(128, 22050, false));
        streamer.SetQuality(192, 48000, true);
        Assert.Equal((192, 48000, true), (streamer.BitrateKbps, streamer.SampleRate, streamer.Mono));

        streamer.SetGain(20);
        Assert.Equal(12, streamer.GainDb);
        streamer.SetGain(-100);
        Assert.Equal(-60, streamer.GainDb);
    }

    [Fact]
    public void SendsTheWholeToneAsMp3AtTheChosenQuality()
    {
        using var server = new FakeIcecast();
        var input = new FakeInput(Rate);
        var titles = new List<string>();
        using var streamer = Create(input, server, titles);
        streamer.SetQuality(128, 44100, mono: false); // la targeta va a 48 kHz: LAME la converteix
        streamer.SetTitle("Directe des de la plaça");

        streamer.Start();
        Assert.Equal(StreamerState.Connecting, streamer.State);
        Assert.Throws<StreamerCommandException>(() => streamer.SetQuality(192, 44100, false));
        WaitFor(streamer, () => streamer.State == StreamerState.Streaming);

        input.PushTone(3);
        streamer.Stop();
        Assert.Equal(StreamerState.Idle, streamer.State);

        var mp3 = server.WaitForConnection(0);
        var analysis = Mp3Frames.Analyze(mp3);
        Assert.Equal(44100, analysis.SampleRate);
        Assert.Equal([128], analysis.Bitrates);
        Assert.InRange(analysis.Duration.TotalSeconds, 2.95, 3.15);
        Assert.Equal(0, analysis.TrailingBytes);
        Assert.Equal(mp3.Length, streamer.BytesSent);
        Assert.Equal(0, streamer.BytesDropped);
        Assert.Equal(0, streamer.SamplesLost);
        Assert.Contains("PUT /stream HTTP/1.1", server.Headers(0));

        WaitFor(streamer, () => { lock (titles) return titles.Count > 0; });
        Assert.Equal("Directe des de la plaça", titles[0]);
    }

    [Fact]
    public void LevelerOnAndOffWhileStreamingLosesNothing()
    {
        using var server = new FakeIcecast();
        var input = new FakeInput(Rate);
        using var streamer = Create(input, server);
        streamer.SetQuality(128, 48000, mono: false);
        streamer.Start();
        WaitFor(streamer, () => streamer.State == StreamerState.Streaming);

        input.PushTone(2);
        streamer.SetLeveler(true);
        input.PushTone(2);
        Thread.Sleep(300);
        streamer.SetLeveler(false);
        input.PushTone(2);
        streamer.Stop();

        var analysis = Mp3Frames.Analyze(server.WaitForConnection(0));
        Assert.InRange(analysis.Duration.TotalSeconds, 5.95, 6.15);
        Assert.Null(streamer.LevelerGainDb);
    }

    [Fact]
    public void ReconnectsByItselfWhenTheServerCutsTheConnection()
    {
        using var server = new FakeIcecast { CloseFirstAfterBytes = 4000 };
        var input = new FakeInput(Rate);
        var titles = new List<string>();
        using var streamer = Create(input, server, titles);
        streamer.SetTitle("Directe");
        streamer.Start();
        WaitFor(streamer, () => streamer.State == StreamerState.Streaming);

        // So continu mentre el servidor talla la primera connexió.
        using var cts = new CancellationTokenSource();
        var pushing = Task.Run(() => { while (!cts.IsCancellationRequested) { input.PushTone(0.1); Thread.Sleep(100); } });
        bool sawReconnecting = false;
        WaitFor(streamer, () =>
        {
            sawReconnecting |= streamer.State == StreamerState.Reconnecting;
            return streamer.Reconnects == 1 && streamer.State == StreamerState.Streaming;
        });
        cts.Cancel();
        pushing.Wait();
        streamer.Stop();

        Assert.True(server.Connections >= 2);
        Assert.True(Mp3Frames.Analyze(server.WaitForConnection(1)).Frames > 0);
        WaitFor(streamer, () => { lock (titles) return titles.Count >= 2; }); // el títol es torna a enviar en reconnectar
        _ = sawReconnecting; // pot ser massa ràpid per veure'l amb Tick; el que compta és Reconnects
    }

    [Fact]
    public void KeepsTryingWhileTheServerIsMissing()
    {
        int port;
        using (var probe = new FakeIcecast()) port = probe.Port; // port lliure: no hi ha ningú

        var input = new FakeInput(Rate);
        using var streamer = new Streamer("1", _ => input, (_, _) => { }) { RetryDelay = TimeSpan.FromMilliseconds(100) };
        streamer.SetDevice("entrada");
        streamer.SetServer(new StreamServer { Type = StreamServerType.Icecast, Host = "127.0.0.1", Port = port });
        streamer.Start();

        WaitFor(streamer, () => streamer.ConnectionError is not null);
        Assert.Equal(StreamerState.Connecting, streamer.State);
        Assert.Contains("No es pot connectar", streamer.ConnectionError);
        streamer.Stop();
        Assert.Equal(StreamerState.Idle, streamer.State);
    }

    [Fact]
    public void LosingTheSourceStopsTheStreamWithAnError()
    {
        using var server = new FakeIcecast();
        var input = new FakeInput(Rate);
        using var streamer = Create(input, server);
        streamer.Start();
        WaitFor(streamer, () => streamer.State == StreamerState.Streaming);

        input.PushTone(0.5);
        input.Fail("USB desconnectat");
        WaitFor(streamer, () => streamer.State == StreamerState.Error);
        Assert.Contains("Font no disponible", streamer.ErrorMessage);
        server.WaitForConnection(0); // la connexió s'ha tancat
    }

    [Fact]
    public void GainIsApplied()
    {
        using var server = new FakeIcecast();
        var input = new FakeInput(Rate);
        using var streamer = Create(input, server);
        streamer.SetGain(-60); // silenci
        Assert.Equal(-60, streamer.GainDb);
        streamer.Start();
        WaitFor(streamer, () => streamer.State == StreamerState.Streaming);
        input.PushTone(1);
        Assert.Equal(0f, streamer.Meter.Take().Left);
        streamer.Stop();

        // Descodificat, el so és pràcticament silenci.
        var mp3 = server.WaitForConnection(0);
        string path = Path.Combine(Path.GetTempPath(), $"stream-{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(path, mp3);
        try
        {
            using var source = AudioFileSource.Open(path, Rate);
            float peak = TestAudio.ReadAll(source).Max(Math.Abs);
            Assert.True(peak < 0.001f, $"pic {peak}");
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Servidor Icecast fals: accepta connexions una darrere l'altra, respon «100 Continue» i desa el que rep.
    /// Amb <see cref="CloseFirstAfterBytes"/>, talla la primera connexió després de rebre aquests bytes.
    /// </summary>
    private sealed class FakeIcecast : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly ConcurrentDictionary<int, (List<string> Headers, MemoryStream Data, TaskCompletionSource Done)> _connections = new();
        private readonly CancellationTokenSource _cts = new();
        private int _count;

        public FakeIcecast()
        {
            _listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int CloseFirstAfterBytes { get; init; }
        public int Connections => Volatile.Read(ref _count);

        public List<string> Headers(int index) => _connections[index].Headers;

        /// <summary>Espera que la connexió número <paramref name="index"/> s'hagi tancat i en retorna les dades.</summary>
        public byte[] WaitForConnection(int index)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!_connections.ContainsKey(index))
            {
                if (DateTime.UtcNow > until) throw new TimeoutException("No hi ha hagut aquesta connexió.");
                Thread.Sleep(10);
            }
            Assert.True(_connections[index].Done.Task.Wait(TimeSpan.FromSeconds(10)), "La connexió no s'ha tancat.");
            return _connections[index].Data.ToArray();
        }

        private async Task AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { return; }
                int index = Interlocked.Increment(ref _count) - 1;
                var entry = (new List<string>(), new MemoryStream(), new TaskCompletionSource());
                _connections[index] = entry;
                _ = Task.Run(() => Serve(client, index, entry));
            }
        }

        private void Serve(TcpClient client, int index, (List<string> Headers, MemoryStream Data, TaskCompletionSource Done) entry)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    for (string line; (line = ReadLine(stream)).Length > 0;) entry.Headers.Add(line);
                    stream.Write(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"));
                    var buffer = new byte[8192];
                    int limit = index == 0 && CloseFirstAfterBytes > 0 ? CloseFirstAfterBytes : int.MaxValue;
                    while (entry.Data.Length < limit)
                    {
                        int n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - entry.Data.Length));
                        if (n == 0) break;
                        lock (entry.Data) entry.Data.Write(buffer, 0, n);
                    }
                    if (entry.Data.Length >= limit) client.Client.LingerState = new LingerOption(true, 0); // tall brusc
                }
                catch (IOException) { }
                finally { entry.Done.TrySetResult(); }
            }
        }

        private static string ReadLine(NetworkStream stream)
        {
            var bytes = new List<byte>();
            for (int b; (b = stream.ReadByte()) >= 0 && b != '\n';)
                if (b != '\r') bytes.Add((byte)b);
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }
}
