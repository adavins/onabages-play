using NAudio.Wave;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>
/// L'anivellador al reproductor (fase 5, pas 30b; D-135): la pista sona a l'objectiu, el NORM no s'hi suma, es pot
/// activar mentre sona (sense tallar ni moure la posició) o en pausa, el guany és el del que sona, i les ordres.
/// </summary>
public class PlayerLevelerTests : IDisposable
{
    private const string DeviceId = "{0.0.0.00000000}.{proves}";
    private const int Rate = 48000;
    private const double Target = -16;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "anivellador-" + Guid.NewGuid().ToString("N"));
    private FakeOutput? _output;

    public PlayerLevelerTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>WAV de «parla» (400 ms de to, 200 ms de silenci) a <paramref name="peakDb"/> de pic.</summary>
    private string Speech(double seconds, double peakDb)
    {
        string path = Path.Combine(_folder, $"parla-{peakDb}-{Guid.NewGuid():N}.wav");
        float amplitude = (float)Math.Pow(10, peakDb / 20);
        int frames = (int)(seconds * Rate);
        var samples = new float[frames * 2];
        for (int t = 0; t < frames; t++)
        {
            bool sound = t % (Rate * 6 / 10) < Rate * 4 / 10;
            float s = sound ? amplitude * (float)Math.Sin(2 * Math.PI * (300 + 150 * (t / (Rate * 6 / 10) % 5)) * t / Rate) : 0f;
            samples[t * 2] = samples[t * 2 + 1] = s;
        }
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2));
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private Player NewPlayer()
    {
        var player = new Player("1", _ => _output = new FakeOutput(), new TrackAnalyzer(synchronous: true));
        player.SetDevice(DeviceId);
        player.SetNormalization(false, Target); // l'objectiu (també el de l'anivellador)
        return player;
    }

    /// <summary>Fa de fil d'àudio durant <paramref name="seconds"/> (blocs de 10 ms) i retorna el que ha sortit.</summary>
    private float[] Pump(Player player, double seconds)
    {
        var all = new List<float>();
        for (int n = 0; n < (int)(seconds * 100); n++)
        {
            all.AddRange(_output!.Pump(480));
            player.Tick();
            if (n % 5 == 0) Thread.Sleep(1);
        }
        return [.. all];
    }

    private static double Lufs(float[] samples)
    {
        var meter = new LoudnessMeter(Rate);
        meter.Add(samples);
        return meter.IntegratedLufs!.Value;
    }

    [Fact]
    public void Una_pista_fluixa_sona_a_l_objectiu_i_el_NORM_no_s_hi_suma()
    {
        using var player = NewPlayer();
        string path = Speech(6, -30);
        player.Playlist.Add([path]);
        player.SetNormalization(true, Target);
        player.SetLeveler(true, 12);

        player.Play();
        Thread.Sleep(200);
        Assert.Null(player.NormalizationDb); // amb l'anivellador, cap guany de normalització
        var output = Pump(player, 4);

        // La pista és molt fluixa: l'anivellador la puja el màxim (12 dB).
        double original = TrackLoudness.Measure(path).Lufs!.Value;
        double level = Lufs(output.AsSpan(Rate * 2, Rate * 2 * 3).ToArray());
        Assert.InRange(level - original, 11, 13);
        Assert.InRange(player.LevelerGainDb!.Value, 11.5, 12.01);
        Assert.Equal(0, player.Underruns);
    }

    [Fact]
    public void Una_pista_a_l_abast_queda_a_l_objectiu()
    {
        using var player = NewPlayer();
        player.Playlist.Add([Speech(6, -22)]);
        player.SetLeveler(true, 12);
        player.Play();
        Thread.Sleep(200);
        var output = Pump(player, 4.5);
        Assert.InRange(Lufs(output.AsSpan(Rate * 2, Rate * 2 * 3).ToArray()), Target - 1.5, Target + 1.5);
        Assert.NotNull(player.LevelerGainDb);
    }

    [Fact]
    public void Activar_lo_mentre_sona_no_talla_ni_mou_la_posicio()
    {
        using var player = NewPlayer();
        player.Playlist.Add([Speech(10, -22)]);
        player.Play();
        Thread.Sleep(200);
        var before = Pump(player, 2);
        var position = player.Position;
        Assert.Null(player.LevelerGainDb);

        player.SetLeveler(true, 12);
        Assert.Equal(PlayerState.Playing, player.State);
        Assert.InRange((player.Position - position).TotalSeconds, -0.15, 0.15);
        Thread.Sleep(200);
        var after = Pump(player, 3);

        Assert.Equal(0, player.Underruns);
        // Abans, a −22 dB de pic (uns −23 LUFS); després, a l'objectiu.
        Assert.InRange(Lufs(after.AsSpan(Rate * 2, Rate * 2 * 2).ToArray()) - Lufs(before), 5, 9);
        Assert.NotNull(player.LevelerGainDb);

        player.SetLeveler(false, 12);
        Assert.Null(player.LevelerGainDb);
        Assert.Equal(PlayerState.Playing, player.State);
    }

    [Fact]
    public void En_pausa_continua_en_pausa_al_mateix_punt()
    {
        using var player = NewPlayer();
        player.Playlist.Add([Speech(6, -22)]);
        player.Play();
        Thread.Sleep(200);
        Pump(player, 1.5);
        player.Pause();
        var position = player.Position;

        player.SetLeveler(true, 12);

        Assert.Equal(PlayerState.Paused, player.State);
        Assert.InRange((player.Position - position).TotalSeconds, -0.05, 0.05);
    }

    [Fact]
    public void Ordres_i_perfil()
    {
        var repository = new ConfigRepository(Path.Combine(_folder, "dades", "config.json"));
        var store = new ConfigStore(repository);
        store.Load();
        using var player = NewPlayer();
        var router = new CommandRouter(new EngineState([player], []), () => { }, () => [], config: store);

        var r = router.Handle("""{ "v": 1, "id": 1, "cmd": "player.setLeveler", "player": "1", "enabled": true }""");
        var leveler = r["result"]!["player"]!["leveler"]!;
        Assert.True(leveler["enabled"]!.GetValue<bool>());
        Assert.Equal(12, leveler["maxBoostDb"]!.GetValue<double>());
        Assert.Contains("1", repository.Load().Config.LeveledPlayers);

        r = router.Handle("""{ "v": 1, "id": 2, "cmd": "settings.set", "leveler": { "maxBoostDb": 9 } }""");
        Assert.Equal(9, r["result"]!["settings"]!["leveler"]!["maxBoostDb"]!.GetValue<double>());
        Assert.Equal(9, player.LevelerMaxBoostDb);
        Assert.True(player.Level);
        Assert.Equal(9, repository.Load().Config.LevelerMaxBoostDb);

        Assert.Equal("bad_params", router.Handle("""{ "v": 1, "id": 3, "cmd": "settings.set", "leveler": { "maxBoostDb": 30 } }""")["error"]!["code"]!.GetValue<string>());
        Assert.Equal("bad_params", router.Handle("""{ "v": 1, "id": 4, "cmd": "player.setLeveler", "player": "1" }""")["error"]!["code"]!.GetValue<string>());

        router.Handle("""{ "v": 1, "id": 5, "cmd": "player.setLeveler", "player": "1", "enabled": false }""");
        Assert.False(player.Level);
        Assert.Empty(repository.Load().Config.LeveledPlayers);
        Assert.True(PlayerJson.PositionEvent([player])["players"]![0]!.AsObject().ContainsKey("levelerGainDb"));
    }
}
