using System.Net;
using System.Net.Sockets;
using System.Text;
using OnaBages.Audio;

namespace OnaBages.Tests;

/// <summary>
/// Connexió amb els servidors d'streaming (fase 5, pas 31a). No cal cap servidor de debò:
/// un servidor fals local (<see cref="FakeServer"/>) fa de Icecast o de Shoutcast, apunta
/// què li arriba i respon el que toca.
/// </summary>
public class StreamConnectionTests
{
    private static readonly StreamFormat Format = new(128, 44100, 2);
    private static readonly StreamInfo Info = new() { Name = "Ona Bages", Genre = "Local", Url = "https://onabages.cat" };
    private static readonly byte[] Mp3 = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();

    [Fact]
    public async Task Icecast_SendsPutWithPasswordHeadersAndData()
    {
        using var fake = new FakeServer();
        var received = fake.Serve("HTTP/1.1 100 Continue\r\n\r\n", readBytes: Mp3.Length);

        var server = new StreamServer { Type = StreamServerType.Icecast, Host = "127.0.0.1", Port = fake.Port, Password = "secret", Mount = "directe" };
        using (var connection = StreamConnection.Open(server, Format, Info))
        {
            connection.Write(Mp3);
            Assert.Equal(Mp3.Length, connection.BytesSent);
        }

        var (lines, data) = await received;
        Assert.Equal("PUT /directe HTTP/1.1", lines[0]);
        Assert.Contains("Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("source:secret")), lines);
        Assert.Contains("Content-Type: audio/mpeg", lines);
        Assert.Contains("Ice-Name: Ona Bages", lines);
        Assert.Contains("Ice-Public: 0", lines);
        Assert.Contains("Ice-Audio-Info: ice-samplerate=44100;ice-bitrate=128;ice-channels=2", lines);
        Assert.Equal(Mp3, data);
    }

    [Fact]
    public async Task Icecast_Accepts200OkToo()
    {
        using var fake = new FakeServer();
        var received = fake.Serve("HTTP/1.0 200 OK\r\nServer: Icecast 2.4.4\r\n\r\n", readBytes: 10);

        var server = new StreamServer { Type = StreamServerType.Icecast, Host = "127.0.0.1", Port = fake.Port, Password = "x" };
        using (var connection = StreamConnection.Open(server, Format, Info))
            connection.Write(Mp3.AsSpan(0, 10));

        var (_, data) = await received;
        Assert.Equal(Mp3[..10], data);
    }

    [Theory]
    [InlineData("HTTP/1.1 401 Authentication Required", "contrasenya")]
    [InlineData("HTTP/1.1 403 Mountpoint in use", "ja està en ús")]
    [InlineData("HTTP/1.1 405 Method Not Allowed", "massa antic")]
    [InlineData("invalid password", "no ha respost com un Icecast")]
    public void Icecast_RefusalsGiveAClearMessage(string status, string expected)
    {
        using var fake = new FakeServer();
        _ = fake.Serve(status + "\r\n\r\n", readBytes: 0);

        var server = new StreamServer { Type = StreamServerType.Icecast, Host = "127.0.0.1", Port = fake.Port, Password = "x" };
        var ex = Assert.Throws<StreamException>(() => StreamConnection.Open(server, Format, Info));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task Shoutcast_ConnectsToNextPortWithPasswordAndIcyHeaders()
    {
        using var fake = new FakeServer();
        var received = fake.Serve("OK2\r\nicy-caps:11\r\n\r\n", readBytes: Mp3.Length, firstLineOnly: true);

        // Com al BUTT: es posa el port del servidor i l'emissió va al següent.
        var server = new StreamServer { Type = StreamServerType.Shoutcast, Host = "127.0.0.1", Port = fake.Port - 1, Password = "secret" };
        Assert.Equal(fake.Port, server.SourcePort);
        using (var connection = StreamConnection.Open(server, Format, Info))
            connection.Write(Mp3);

        var (lines, data) = await received;
        Assert.Equal("secret", lines[0]);
        Assert.Contains("content-type:audio/mpeg", lines);
        Assert.Contains("icy-name:Ona Bages", lines);
        Assert.Contains("icy-br:128", lines);
        Assert.Contains("icy-pub:0", lines);
        Assert.Equal(Mp3, data);
    }

    [Fact]
    public async Task Shoutcast_StreamIdGoesWithThePassword()
    {
        using var fake = new FakeServer();
        var received = fake.Serve("OK2\r\nicy-caps:11\r\n\r\n", readBytes: 0, firstLineOnly: true);

        var server = new StreamServer { Type = StreamServerType.Shoutcast, Host = "127.0.0.1", Port = fake.Port - 1, Password = "secret", StreamId = 2 };
        using (StreamConnection.Open(server, Format, Info)) { }

        var (lines, _) = await received;
        Assert.Equal("secret:#2", lines[0]);
    }

    [Fact]
    public void Shoutcast_WrongPassword()
    {
        using var fake = new FakeServer();
        _ = fake.Serve("invalid password\r\n", readBytes: 0, firstLineOnly: true);

        var server = new StreamServer { Type = StreamServerType.Shoutcast, Host = "127.0.0.1", Port = fake.Port - 1, Password = "dolenta" };
        var ex = Assert.Throws<StreamException>(() => StreamConnection.Open(server, Format, Info));
        Assert.Contains("contrasenya", ex.Message);
    }

    [Fact]
    public void NobodyListening_GivesAClearMessage()
    {
        int port;
        using (var fake = new FakeServer()) port = fake.Port; // el port queda lliure

        var server = new StreamServer { Type = StreamServerType.Icecast, Host = "127.0.0.1", Port = port };
        var ex = Assert.Throws<StreamException>(() => StreamConnection.Open(server, Format, Info));
        Assert.Contains("No es pot connectar", ex.Message);
    }

    [Fact]
    public void ServerThatNeverAnswers_TimesOut()
    {
        using var fake = new FakeServer();
        _ = fake.AcceptAndWait();

        var server = new StreamServer { Type = StreamServerType.Shoutcast, Host = "127.0.0.1", Port = fake.Port - 1, Password = "x" };
        var started = DateTime.UtcNow;
        var ex = Assert.Throws<StreamException>(() => StreamConnection.Open(server, Format, Info, TimeSpan.FromMilliseconds(300)));
        Assert.Contains("no respon", ex.Message);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task LineBreaksInNamesCannotAddHeaders()
    {
        using var fake = new FakeServer();
        var received = fake.Serve("HTTP/1.1 100 Continue\r\n\r\n", readBytes: 0);

        var server = new StreamServer { Type = StreamServerType.Icecast, Host = "127.0.0.1", Port = fake.Port, Password = "x" };
        var info = Info with { Name = "Ona\r\nIce-Public: 1" };
        using (StreamConnection.Open(server, Format, info)) { }

        var (lines, _) = await received;
        Assert.Contains("Ice-Name: Ona Ice-Public: 1", lines);
        Assert.Single(lines, l => l.StartsWith("Ice-Public"));
    }

    [Fact]
    public void TitleUri_Icecast()
    {
        var server = new StreamServer { Type = StreamServerType.Icecast, Host = "radio.example", Port = 8000, Mount = "/radio.mp3" };
        var uri = StreamMetadata.BuildUri(server, "Directe des de la plaça");
        Assert.Equal("http://radio.example:8000/admin/metadata?mount=%2Fradio.mp3&mode=updinfo&song=Directe%20des%20de%20la%20pla%C3%A7a&charset=UTF-8", uri.AbsoluteUri);
    }

    [Fact]
    public void TitleUri_Shoutcast()
    {
        var server = new StreamServer { Type = StreamServerType.Shoutcast, Host = "radio.example", Port = 8000, Password = "a&b", StreamId = 1 };
        var uri = StreamMetadata.BuildUri(server, "Directe");
        Assert.Equal("http://radio.example:8000/admin.cgi?sid=1&mode=updinfo&song=Directe&pass=a%26b", uri.AbsoluteUri);
    }

    [Fact]
    public async Task SetTitle_Icecast_SendsPasswordAndTitle()
    {
        using var fake = new FakeServer();
        var received = fake.Serve("HTTP/1.0 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", readBytes: 0);

        var server = new StreamServer { Type = StreamServerType.Icecast, Host = "127.0.0.1", Port = fake.Port, Password = "secret", Mount = "/radio.mp3" };
        StreamMetadata.SetTitle(server, "Directe des de la plaça");

        var (lines, _) = await received;
        Assert.StartsWith("GET /admin/metadata?mount=%2Fradio.mp3&mode=updinfo&song=Directe%20des%20de%20la%20pla%C3%A7a", lines[0]);
        Assert.Contains("Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("source:secret")), lines);
    }

    [Fact]
    public async Task SetTitle_Shoutcast_UsesMainPort()
    {
        using var fake = new FakeServer();
        var received = fake.Serve("HTTP/1.0 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", readBytes: 0);

        var server = new StreamServer { Type = StreamServerType.Shoutcast, Host = "127.0.0.1", Port = fake.Port, Password = "secret" };
        StreamMetadata.SetTitle(server, "Directe");

        var (lines, _) = await received;
        Assert.StartsWith("GET /admin.cgi?sid=1&mode=updinfo&song=Directe&pass=secret", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("User-Agent: Mozilla/5.0"));
    }

    [Fact]
    public void SetTitle_Refused()
    {
        using var fake = new FakeServer();
        _ = fake.Serve("HTTP/1.0 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", readBytes: 0);

        var server = new StreamServer { Type = StreamServerType.Icecast, Host = "127.0.0.1", Port = fake.Port, Password = "x" };
        var ex = Assert.Throws<StreamException>(() => StreamMetadata.SetTitle(server, "Directe"));
        Assert.Contains("contrasenya", ex.Message);
    }

    /// <summary>
    /// Servidor fals: accepta una connexió, en llegeix la capçalera (fins a la línia buida, o només
    /// la primera línia), respon i llegeix un nombre fix de bytes de dades.
    /// </summary>
    private sealed class FakeServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public FakeServer() => _listener.Start();

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Task<(List<string> Lines, byte[] Data)> Serve(string reply, int readBytes, bool firstLineOnly = false) => Task.Run(async () =>
        {
            using var client = await _listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var stream = client.GetStream();
            stream.ReadTimeout = 10_000;
            var lines = new List<string>();

            if (firstLineOnly)
            {
                // Shoutcast: primer la contrasenya, després la resposta i després les capçaleres icy.
                lines.Add(ReadLine(stream));
                stream.Write(Encoding.UTF8.GetBytes(reply));
                if (reply.StartsWith("OK"))
                    for (string line; (line = ReadLine(stream)).Length > 0;) lines.Add(line);
            }
            else
            {
                for (string line; (line = ReadLine(stream)).Length > 0;) lines.Add(line);
                stream.Write(Encoding.UTF8.GetBytes(reply));
            }

            var data = new byte[readBytes];
            int read = 0;
            while (read < readBytes)
            {
                int n = stream.Read(data, read, readBytes - read);
                if (n == 0) break;
                read += n;
            }
            return (lines, data[..read]);
        });

        /// <summary>Accepta la connexió i no respon mai.</summary>
        public Task AcceptAndWait() => Task.Run(async () =>
        {
            using var client = await _listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(2000);
        });

        private static string ReadLine(NetworkStream stream)
        {
            var bytes = new List<byte>();
            for (int b; (b = stream.ReadByte()) >= 0 && b != '\n';)
                if (b != '\r') bytes.Add((byte)b);
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        public void Dispose() => _listener.Stop();
    }
}
