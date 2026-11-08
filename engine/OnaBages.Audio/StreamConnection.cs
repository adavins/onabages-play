using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace OnaBages.Audio;

/// <summary>Tipus de servidor d'streaming.</summary>
public enum StreamServerType
{
    /// <summary>Icecast 2.4 o més nou (connexió HTTP PUT).</summary>
    Icecast,

    /// <summary>Shoutcast v2 (i v1) en mode «legacy», com el BUTT.</summary>
    Shoutcast,
}

/// <summary>
/// Dades de connexió d'un servidor d'streaming. Són les mateixes que es posen al BUTT.
/// </summary>
public sealed record StreamServer
{
    public StreamServerType Type { get; init; } = StreamServerType.Shoutcast;

    /// <summary>Nom o adreça del servidor, sense «http://» (p. ex. «onabagescast.radioca.st»).</summary>
    public string Host { get; init; } = "";

    /// <summary>
    /// Port del servidor, el mateix que al BUTT. A Shoutcast l'emissió es connecta al port
    /// següent (<see cref="SourcePort"/>), com fan el BUTT i el Shoutcast en mode «legacy».
    /// </summary>
    public int Port { get; init; } = 8000;

    public string Password { get; init; } = "";

    /// <summary>Només Icecast: usuari de la font (gairebé sempre «source»).</summary>
    public string User { get; init; } = "source";

    /// <summary>Només Icecast: punt de muntatge (p. ex. «/radio.mp3»).</summary>
    public string Mount { get; init; } = "/stream";

    /// <summary>Només Shoutcast v2: identificador del stream (sid). Per defecte, 1.</summary>
    public int StreamId { get; init; } = 1;

    /// <summary>Port on es connecta l'emissió: el mateix a Icecast; el següent a Shoutcast.</summary>
    public int SourcePort => Type == StreamServerType.Shoutcast ? Port + 1 : Port;
}

/// <summary>Format de l'àudio que s'envia (s'anuncia al servidor i als oients).</summary>
public sealed record StreamFormat(int BitrateKbps, int SampleRate, int Channels);

/// <summary>Descripció de l'emissió que veuen els oients (nom de l'emissora, gènere, web).</summary>
public sealed record StreamInfo
{
    public string Name { get; init; } = "";
    public string Genre { get; init; } = "";
    public string Url { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>Si s'ha d'anunciar als directoris públics de ràdios. Per defecte, no.</summary>
    public bool Public { get; init; }
}

/// <summary>Error de connexió amb un servidor d'streaming, amb un missatge per a l'operador.</summary>
public sealed class StreamException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Connexió d'emissió amb un servidor Icecast o Shoutcast: s'obre, s'hi escriuen les dades MP3
/// tal com surten del codificador i es tanca.
///
/// És <b>bloquejant</b> (connectar i escriure esperen la xarxa, amb un temps màxim): només s'ha de
/// fer servir des d'un fil propi d'enviament, mai des del fil d'àudio ni des del de codificació.
/// No és segura entre fils. No es reconnecta sola: d'això se n'encarrega qui la fa servir.
/// </summary>
public sealed class StreamConnection : IDisposable
{
    /// <summary>Temps màxim per connectar, per esperar la resposta i per escriure.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private const int MaxLineLength = 1024;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    private StreamConnection(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    /// <summary>Bytes enviats des que s'ha obert la connexió.</summary>
    public long BytesSent { get; private set; }

    /// <summary>
    /// Es connecta al servidor, s'identifica i anuncia l'emissió. En tornar, ja s'hi poden escriure dades.
    /// </summary>
    /// <exception cref="StreamException">Si no es pot connectar o el servidor no accepta l'emissió.</exception>
    public static StreamConnection Open(StreamServer server, StreamFormat format, StreamInfo info, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        if (string.IsNullOrWhiteSpace(server.Host))
            throw new StreamException("Falta l'adreça del servidor.");

        var client = new TcpClient { NoDelay = true };
        try
        {
            try
            {
                using var cts = new CancellationTokenSource(limit);
                client.ConnectAsync(server.Host.Trim(), server.SourcePort, cts.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException ex)
            {
                throw new StreamException($"El servidor {server.Host}:{server.SourcePort} no respon.", ex);
            }
            catch (SocketException ex)
            {
                throw new StreamException($"No es pot connectar amb {server.Host}:{server.SourcePort} ({ex.SocketErrorCode}).", ex);
            }

            var connection = new StreamConnection(client);
            connection._stream.ReadTimeout = (int)limit.TotalMilliseconds;
            connection._stream.WriteTimeout = (int)limit.TotalMilliseconds;

            try
            {
                if (server.Type == StreamServerType.Icecast)
                    connection.HandshakeIcecast(server, format, info);
                else
                    connection.HandshakeShoutcast(server, format, info);
            }
            catch (IOException ex)
            {
                throw new StreamException($"El servidor {server.Host} ha tallat la connexió o no respon.", ex);
            }

            return connection;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Envia dades MP3. Espera la xarxa com a molt el temps màxim.</summary>
    /// <exception cref="StreamException">Si la connexió s'ha perdut.</exception>
    public void Write(ReadOnlySpan<byte> data)
    {
        try
        {
            _stream.Write(data);
            BytesSent += data.Length;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            throw new StreamException("S'ha perdut la connexió amb el servidor.", ex);
        }
    }

    public void Dispose()
    {
        try { _stream.Dispose(); } catch (IOException) { }
        _client.Dispose();
    }

    // Icecast 2.4+: una petició HTTP PUT que no s'acaba mai (el cos són les dades MP3).
    private void HandshakeIcecast(StreamServer server, StreamFormat format, StreamInfo info)
    {
        string mount = server.Mount.Trim();
        if (!mount.StartsWith('/')) mount = "/" + mount;
        string auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Clean(server.User)}:{server.Password}"));

        var request = new StringBuilder()
            .Append($"PUT {Clean(mount)} HTTP/1.1\r\n")
            .Append($"Host: {Clean(server.Host.Trim())}:{server.Port}\r\n")
            .Append($"Authorization: Basic {auth}\r\n")
            .Append($"User-Agent: {UserAgent}\r\n")
            .Append("Content-Type: audio/mpeg\r\n")
            .Append($"Ice-Public: {(info.Public ? 1 : 0)}\r\n")
            .Append($"Ice-Name: {Clean(info.Name)}\r\n")
            .Append($"Ice-Description: {Clean(info.Description)}\r\n")
            .Append($"Ice-Genre: {Clean(info.Genre)}\r\n")
            .Append($"Ice-Url: {Clean(info.Url)}\r\n")
            .Append($"Ice-Audio-Info: ice-samplerate={format.SampleRate};ice-bitrate={format.BitrateKbps};ice-channels={format.Channels}\r\n")
            .Append("Expect: 100-continue\r\n")
            .Append("\r\n");
        SendText(request.ToString());

        // Resposta: «HTTP/1.1 100 Continue» (o «200 OK»), seguida de capçaleres i una línia buida.
        string status = ReadLine();
        while (ReadLine().Length > 0) { }

        int code = ParseHttpCode(status);
        if (code is 100 or 200) return;
        throw code switch
        {
            401 => new StreamException("El servidor no accepta la contrasenya (o l'usuari)."),
            403 => new StreamException($"El punt de muntatge {mount} ja està en ús o no està permès."),
            400 or 405 or 501 => new StreamException("El servidor Icecast és massa antic (cal la versió 2.4 o més nova)."),
            0 => new StreamException("El servidor no ha respost com un Icecast. Comproveu el tipus de servidor i el port."),
            _ => new StreamException($"El servidor no accepta l'emissió ({status.Trim()})."),
        };
    }

    // Shoutcast «legacy» (el que fa servir el BUTT): contrasenya, «OK2» i capçaleres icy.
    private void HandshakeShoutcast(StreamServer server, StreamFormat format, StreamInfo info)
    {
        // Shoutcast v2: «contrasenya:#sid» tria el stream; el sid 1 també funciona sense res.
        string password = server.StreamId > 1 ? $"{server.Password}:#{server.StreamId}" : server.Password;
        SendText(Clean(password) + "\r\n");

        string answer = ReadLine();
        if (!answer.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
        {
            throw answer.Contains("password", StringComparison.OrdinalIgnoreCase)
                ? new StreamException("El servidor no accepta la contrasenya.")
                : answer.Contains("in use", StringComparison.OrdinalIgnoreCase)
                    ? new StreamException("El servidor ja rep una altra emissió en aquest stream.")
                    : new StreamException(answer.Length == 0
                        ? "El servidor no ha respost. Comproveu el tipus de servidor i el port."
                        : $"El servidor no accepta l'emissió ({answer.Trim()}).");
        }

        // «OK2» va seguit de capçaleres (icy-caps) i una línia buida; «OK» sol, no.
        if (answer.StartsWith("OK2", StringComparison.OrdinalIgnoreCase))
            while (ReadLine().Length > 0) { }

        var headers = new StringBuilder()
            .Append("content-type:audio/mpeg\r\n")
            .Append($"icy-name:{Clean(info.Name)}\r\n")
            .Append($"icy-genre:{Clean(info.Genre)}\r\n")
            .Append($"icy-url:{Clean(info.Url)}\r\n")
            .Append($"icy-pub:{(info.Public ? 1 : 0)}\r\n")
            .Append($"icy-br:{format.BitrateKbps}\r\n")
            .Append("\r\n");
        SendText(headers.ToString());
    }

    private void SendText(string text) => _stream.Write(Encoding.UTF8.GetBytes(text));

    // Llegeix una línia acabada en \n (sense \r\n). Buida si el servidor tanca la connexió.
    private string ReadLine()
    {
        var bytes = new List<byte>();
        while (bytes.Count < MaxLineLength)
        {
            int b = _stream.ReadByte();
            if (b < 0 || b == '\n') break;
            if (b != '\r') bytes.Add((byte)b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static int ParseHttpCode(string statusLine)
    {
        // «HTTP/1.1 100 Continue» → 100
        var parts = statusLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(parts[1], out int code) ? code : 0;
    }

    /// <summary>Treu els salts de línia: un nom amb «\r\n» no pot afegir capçaleres de més.</summary>
    internal static string Clean(string? value) =>
        (value ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();

    internal static string UserAgent => $"OnaBagesPlay/{typeof(StreamConnection).Assembly.GetName().Version?.ToString(3)}";
}

/// <summary>
/// Envia el títol que veuen els oients («Directe des de la plaça»). Fa una petició HTTP a part,
/// independent de la connexió d'emissió (és com ho fan el BUTT i els servidors).
/// Bloquejant (fins a 5 s): s'ha de fer servir des d'un fil que no sigui el d'àudio.
/// </summary>
public static class StreamMetadata
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static readonly HttpClient Http = new() { Timeout = Timeout };

    /// <summary>
    /// Adreça de la petició que canvia el títol. A Shoutcast hi va la contrasenya: no s'ha d'escriure al registre.
    /// </summary>
    public static Uri BuildUri(StreamServer server, string title)
    {
        string song = Uri.EscapeDataString(StreamConnection.Clean(title));
        string host = server.Host.Trim();
        if (server.Type == StreamServerType.Icecast)
        {
            string mount = server.Mount.Trim();
            if (!mount.StartsWith('/')) mount = "/" + mount;
            return new Uri($"http://{host}:{server.Port}/admin/metadata?mount={Uri.EscapeDataString(mount)}&mode=updinfo&song={song}&charset=UTF-8");
        }

        // Shoutcast: al port principal (no al de l'emissió) i amb la contrasenya de l'emissió.
        return new Uri($"http://{host}:{server.Port}/admin.cgi?sid={server.StreamId}&mode=updinfo&song={song}&pass={Uri.EscapeDataString(server.Password)}");
    }

    /// <summary>Canvia el títol de l'emissió al servidor.</summary>
    /// <exception cref="StreamException">Si el servidor no respon o no accepta el canvi.</exception>
    public static void SetTitle(StreamServer server, string title)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(server, title));
        // Alguns Shoutcast només responen als navegadors: com el BUTT, ens hi presentem com a «Mozilla».
        request.Headers.TryAddWithoutValidation("User-Agent", $"Mozilla/5.0 (compatible; {StreamConnection.UserAgent})");
        if (server.Type == StreamServerType.Icecast)
        {
            string auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{StreamConnection.Clean(server.User)}:{server.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
        }

        HttpResponseMessage response;
        try
        {
            response = Http.Send(request);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new StreamException("No s'ha pogut enviar el títol: el servidor no respon.", ex);
        }

        using (response)
        {
            int code = (int)response.StatusCode;
            if (code is >= 200 and < 300) return;
            throw code is 401 or 403
                ? new StreamException("El servidor no accepta la contrasenya per canviar el títol.")
                : new StreamException($"El servidor no ha acceptat el títol (error {code}).");
        }
    }
}
