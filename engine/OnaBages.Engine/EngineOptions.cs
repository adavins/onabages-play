using System.Security.Cryptography;
using System.Text;

namespace OnaBages.Engine;

/// <summary>
/// Opcions d'arrencada del motor, llegides de la línia d'ordres.
/// </summary>
public sealed record EngineOptions(int Port, string Token)
{
    public const int DefaultPort = 47800;

    public static EngineOptions Parse(string[] args)
    {
        int port = DefaultPort;
        string? token = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length:
                    string value = args[++i];
                    if (!int.TryParse(value, out port) || port < 1 || port > 65535)
                        throw new ArgumentException($"Port no vàlid: {value}");
                    break;

                case "--token" when i + 1 < args.Length:
                    token = args[++i];
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(token))
            token = NewToken();

        return new EngineOptions(port, token);
    }

    /// <summary>Genera un testimoni aleatori (48 caràcters hexadecimals).</summary>
    public static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    /// <summary>Compara testimonis en temps constant.</summary>
    public static bool TokenMatches(string? received, string expected)
    {
        if (string.IsNullOrEmpty(received))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(received),
            Encoding.UTF8.GetBytes(expected));
    }
}
