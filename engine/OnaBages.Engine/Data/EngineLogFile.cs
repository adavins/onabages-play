using System.Collections.Concurrent;
using System.Text;

namespace OnaBages.Engine.Data;

/// <summary>
/// Registre tècnic del motor en fitxer (especificació 13): un fitxer de text per dia a
/// <c>%APPDATA%\OnaBages\logs\motor-AAAA-MM-DD.log</c>, i s'esborren els de fa més de
/// <see cref="KeepDays"/> dies. Forma part del mòdul de dades.
///
/// Qui escriu al registre no espera mai el disc: la línia es posa a la cua i un fil propi
/// l'escriu (i la força al disc) de seguida. Si el disc falla, el motor continua igualment.
/// </summary>
public sealed class EngineLogFile : IDisposable
{
    public const int KeepDays = 30;

    /// <summary>Si la cua creix més d'això (disc molt lent o penjat), les línies noves es descarten i es compten.</summary>
    private const int MaxQueued = 10_000;

    private readonly string _folder;
    private readonly Func<DateTime> _now;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _writer;
    private int _dropped;
    private int _pending; // línies a la cua o escrivint-se
    private DateTime _cleanedDay;

    /// <param name="folder">Carpeta dels registres. Per defecte, la de l'usuari; les proves en passen una de temporal.</param>
    /// <param name="now">Rellotge (les proves en fan servir un de fals).</param>
    public EngineLogFile(string? folder = null, Func<DateTime>? now = null)
    {
        _folder = folder ?? DefaultFolder;
        _now = now ?? (() => DateTime.Now);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Registre del motor" };
        _writer.Start();
    }

    public static string DefaultFolder => Path.Combine(RuntimeFile.DirectoryPath, "logs");

    public string Folder => _folder;

    /// <summary>Fitxer d'un dia.</summary>
    public string PathFor(DateTime day) => Path.Combine(_folder, $"motor-{day:yyyy-MM-dd}.log");

    /// <summary>Afegeix una línia (amb l'hora i el nivell davant). No bloqueja mai.</summary>
    public void Write(string level, string message)
    {
        if (_queue.Count >= MaxQueued)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }
        var now = _now();
        Interlocked.Increment(ref _pending);
        _queue.Enqueue($"{now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {message}");
        _signal.Release();
    }

    /// <summary>Espera (com a molt <paramref name="timeout"/>) que tot el que hi ha a la cua sigui al disc.</summary>
    public void Flush(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _pending) > 0 && DateTime.UtcNow < until)
            Thread.Sleep(10);
    }

    public void Dispose()
    {
        Flush(TimeSpan.FromSeconds(2));
        _stop.Cancel();
        _writer.Join(TimeSpan.FromSeconds(2));
    }

    private void WriteLoop()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        while (!_stop.IsCancellationRequested)
        {
            try { _signal.Wait(_stop.Token); }
            catch (OperationCanceledException) { break; }

            // Totes les línies que hi hagi, juntes, en una sola escriptura per fitxer.
            var lines = new StringBuilder();
            int count = 0;
            while (_queue.TryDequeue(out var line))
            {
                lines.Append(line).Append("\r\n");
                count++;
            }
            int dropped = Interlocked.Exchange(ref _dropped, 0);
            if (dropped > 0)
                lines.Append($"{_now():yyyy-MM-dd HH:mm:ss.fff} WARN  S'han perdut {dropped} línies del registre (el disc no anava prou ràpid)\r\n");
            if (lines.Length == 0) continue;

            try
            {
                var today = _now().Date;
                Directory.CreateDirectory(_folder);
                if (today != _cleanedDay)
                {
                    _cleanedDay = today;
                    DeleteOld(today);
                }
                // FileShare.ReadWrite: es pot obrir amb el Bloc de notes mentre el motor hi escriu.
                using var stream = new FileStream(PathFor(today), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.Write(utf8.GetBytes(lines.ToString()));
                stream.Flush(flushToDisk: true);
            }
            catch (Exception)
            {
                // Sense registre en fitxer el motor ha de continuar: les línies ja han sortit per la consola.
            }
            finally
            {
                Interlocked.Add(ref _pending, -count);
            }
        }
    }

    /// <summary>Esborra els registres de fa més de <see cref="KeepDays"/> dies (només els que tenen el nom del motor).</summary>
    private void DeleteOld(DateTime today)
    {
        foreach (string path in Directory.EnumerateFiles(_folder, "motor-*.log"))
        {
            string date = Path.GetFileNameWithoutExtension(path)["motor-".Length..];
            if (DateTime.TryParseExact(date, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var day)
                && day < today.AddDays(-KeepDays))
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}

/// <summary>Connecta <see cref="EngineLogFile"/> amb el sistema de registre d'ASP.NET (el mateix <c>log</c> que surt per la consola).</summary>
public sealed class EngineLogFileProvider(EngineLogFile file) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(file);

    public void Dispose() { /* el fitxer el tanca qui l'ha creat */ }

    private sealed class FileLogger(EngineLogFile file) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string level = logLevel switch
            {
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERROR",
                LogLevel.Critical => "CRIT",
                _ => "INFO",
            };
            string message = formatter(state, exception);
            if (exception is not null)
                message += Environment.NewLine + exception;
            file.Write(level, message);
        }
    }
}
