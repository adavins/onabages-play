using Microsoft.Extensions.Logging;
using OnaBages.Engine.Data;

namespace OnaBages.Tests;

/// <summary>Registre tècnic del motor en fitxer: un per dia, es guarden 30 dies, i no bloqueja qui escriu.</summary>
public class EngineLogFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "logs-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 11, 7, 10, 0, 0);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private EngineLogFile Open() => new(_folder, () => _now);

    [Fact]
    public void Les_linies_van_al_fitxer_del_dia_amb_l_hora_i_el_nivell()
    {
        using (var log = Open())
        {
            log.Write("INFO", "Reproductor 1: playing cançó.mp3");
            log.Write("WARN", "Enregistrador 2: error");
        }

        string[] lines = File.ReadAllLines(Path.Combine(_folder, "motor-2026-11-07.log"));
        Assert.Equal(2, lines.Length);
        Assert.Equal("2026-11-07 10:00:00.000 INFO  Reproductor 1: playing cançó.mp3", lines[0]);
        Assert.StartsWith("2026-11-07 10:00:00.000 WARN  ", lines[1]);
    }

    [Fact]
    public void Cada_dia_va_a_un_fitxer_nou()
    {
        using (var log = Open())
        {
            log.Write("INFO", "abans de mitjanit");
            log.Flush(TimeSpan.FromSeconds(2));
            _now = new DateTime(2026, 11, 8, 0, 0, 1);
            log.Write("INFO", "després de mitjanit");
        }

        Assert.Single(File.ReadAllLines(Path.Combine(_folder, "motor-2026-11-07.log")));
        Assert.Single(File.ReadAllLines(Path.Combine(_folder, "motor-2026-11-08.log")));
    }

    [Fact]
    public void S_esborren_els_registres_de_fa_mes_de_30_dies()
    {
        Directory.CreateDirectory(_folder);
        string old = Path.Combine(_folder, "motor-2026-10-01.log");
        string recent = Path.Combine(_folder, "motor-2026-10-20.log");
        string other = Path.Combine(_folder, "nota.txt");
        foreach (var path in new[] { old, recent, other })
            File.WriteAllText(path, "x");

        using (var log = Open())
            log.Write("INFO", "arrencada");

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(other)); // només es toquen els fitxers del motor
    }

    [Fact]
    public void Es_pot_llegir_el_fitxer_mentre_s_hi_escriu()
    {
        using var log = Open();
        log.Write("INFO", "primera");
        log.Flush(TimeSpan.FromSeconds(2));

        // Com el Bloc de notes: l'obre mentre el motor continua escrivint.
        using var reader = new FileStream(log.PathFor(_now), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        log.Write("INFO", "segona");
        log.Flush(TimeSpan.FromSeconds(2));

        using var text = new StreamReader(reader);
        Assert.Contains("segona", text.ReadToEnd());
    }

    [Fact]
    public void El_proveidor_escriu_el_que_diu_el_log_del_motor()
    {
        using (var file = Open())
        {
            using var factory = LoggerFactory.Create(b => b.AddProvider(new EngineLogFileProvider(file)));
            var logger = factory.CreateLogger("Motor");
            logger.LogInformation("Reproductor {Id}: {State}", "1", "playing");
            logger.LogDebug("això no hi ha d'anar");
            logger.LogError(new InvalidOperationException("detall"), "Ha fallat");
        }

        string text = File.ReadAllText(Path.Combine(_folder, "motor-2026-11-07.log"));
        Assert.Contains("INFO  Reproductor 1: playing", text);
        Assert.DoesNotContain("això no", text);
        Assert.Contains("ERROR Ha fallat", text);
        Assert.Contains("InvalidOperationException: detall", text);
    }
}
