using NAudio.Wave;
using OnaBages.Audio;

namespace OnaBages.Tests;

/// <summary>
/// Fade-out dels reproductors (fase 5, pas 20; D-107). Pistes d'un valor constant (0,4): així es veu exactament on
/// comença la baixada, quant dura i quin nivell té a cada punt.
/// </summary>
public class FadeOutTests : IDisposable
{
    private const string DeviceId = "{0.0.0.00000000}.{proves}";
    private const int Rate = 48000;
    private const int Block = 480; // 10 ms, com un bloc de la targeta
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "fadeout-" + Guid.NewGuid().ToString("N"));
    private FakeOutput? _output;

    public FadeOutTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private string Constant(float value, double seconds)
    {
        string path = Path.Combine(_folder, $"c-{value}-{Guid.NewGuid():N}.wav");
        var samples = Enumerable.Repeat(value, (int)(Rate * seconds) * 2).ToArray();
        using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2)))
            writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private Player NewPlayer()
    {
        var player = new Player("1", _ => _output = new FakeOutput(), new TrackAnalyzer(synchronous: true));
        player.SetDevice(DeviceId);
        return player;
    }

    /// <summary>Fa de fil d'àudio durant <paramref name="blocks"/> blocs i afegeix el canal esquerre a <paramref name="left"/>.</summary>
    private void Pump(Player player, List<float> left, int blocks)
    {
        for (int b = 0; b < blocks && player.State == PlayerState.Playing; b++)
        {
            var block = _output!.Pump(Block);
            for (int i = 0; i < block.Length; i += 2) left.Add(block[i]);
            player.Tick();
        }
    }

    /// <summary>Fa de fil d'àudio fins que el reproductor s'atura (amb un límit, per si de cas).</summary>
    private void PumpUntilStopped(Player player, List<float> left, double maxSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(maxSeconds);
        while (player.State == PlayerState.Playing && DateTime.UtcNow < deadline)
        {
            Pump(player, left, 1);
            Thread.Sleep(1);
        }
    }

    private (Player Player, string Id) PlayConstant(double seconds = 3)
    {
        var player = NewPlayer();
        var ids = player.Playlist.Add([Constant(0.4f, seconds)]);
        player.Play();
        Thread.Sleep(150); // que el fil de lectura ompli la memòria
        return (player, ids[0]);
    }

    [Fact]
    public void El_so_baixa_el_temps_triat_i_despres_s_atura_com_amb_Stop()
    {
        var (player, id) = PlayConstant();
        var left = new List<float>();
        Pump(player, left, 50); // 0,5 s sencers
        int start = left.Count;

        player.FadeOut(500);
        Assert.True(player.FadingOut);
        PumpUntilStopped(player, left);

        int fade = 500 * Rate / 1000;
        Assert.Equal(0.4f, left[start - 1], 1e-4f);                         // abans: sencer
        // Baixada constant en dB fins a −60 dB: a la meitat, −30 dB (0,4 · 0,0316 ≈ 0,0126).
        Assert.Equal(0.4f * (float)Math.Pow(10, -30.0 / 20), left[start + fade / 2], 0.0005f);
        Assert.Equal(0.4f * 0.001f, left[start + fade - 1], 0.00005f);       // al final, −60 dB
        for (int i = start + 1; i < start + fade; i++)
            Assert.True(left[i] <= left[i - 1], $"ha de baixar sempre (mostra {i - start})");
        Assert.All(left.Skip(start + fade), v => Assert.Equal(0f, v));        // després, silenci
        Assert.True(left.Count - (start + fade) <= 2 * Block, "s'atura de seguida que acaba la baixada");

        Assert.Equal(PlayerState.Ended, player.State);
        Assert.False(player.FadingOut);
        Assert.Equal(id, player.LastPlayedId);                                // com amb Stop: Play farà sonar la següent
        player.Dispose();
    }

    [Fact]
    public void Nomes_es_pot_fer_mentre_sona()
    {
        var player = NewPlayer();
        player.Playlist.Add([Constant(0.4f, 1)]);
        Assert.Throws<PlayerCommandException>(() => player.FadeOut(500)); // buit

        player.Play();
        player.Pause();
        Assert.Throws<PlayerCommandException>(() => player.FadeOut(500)); // en pausa
        player.Dispose();
    }

    [Fact]
    public void Prémer_lo_un_altre_cop_no_el_torna_a_començar()
    {
        var (player, _) = PlayConstant();
        var left = new List<float>();
        player.FadeOut(1000);
        Pump(player, left, 50);                // a mitja baixada (−30 dB)
        player.FadeOut(1000);                  // no fa res
        Pump(player, left, 1);

        Assert.True(left[^1] < 0.4f * 0.04f, "continua baixant des d'on era");
        player.Dispose();
    }

    [Fact]
    public void La_pausa_el_treu_i_en_continuar_sona_al_volum_normal()
    {
        var (player, _) = PlayConstant();
        var left = new List<float>();
        player.FadeOut(1000);
        Pump(player, left, 20);
        Assert.True(left[^1] < 0.4f);

        player.Pause();
        Assert.False(player.FadingOut);
        player.Play();
        left.Clear();
        Pump(player, left, 2);

        Assert.All(left, v => Assert.Equal(0.4f, v, 1e-4f));
        Assert.Equal(PlayerState.Playing, player.State);
        player.Dispose();
    }

    [Fact]
    public void Fer_sonar_una_altra_pista_el_treu()
    {
        var player = NewPlayer();
        var ids = player.Playlist.Add([Constant(0.4f, 3), Constant(0.2f, 3)]);
        player.Play();
        Thread.Sleep(150);
        var left = new List<float>();
        player.FadeOut(1000);
        Pump(player, left, 20);

        player.PlayItem(ids[1]);
        Thread.Sleep(150);
        Assert.False(player.FadingOut);
        left.Clear();
        Pump(player, left, 2);

        Assert.All(left, v => Assert.Equal(0.2f, v, 1e-4f));
        player.Dispose();
    }

    [Fact]
    public void Amb_fos_entre_pistes_fer_sonar_una_altra_pista_tambe_el_treu()
    {
        var player = NewPlayer();
        player.SetCrossfade(100);
        var ids = player.Playlist.Add([Constant(0.4f, 3), Constant(0.2f, 3)]);
        player.Play();
        Thread.Sleep(150);
        var left = new List<float>();
        player.FadeOut(1000);
        Pump(player, left, 20);

        player.PlayItem(ids[1]); // la mateixa sortida continua: el fade-out s'ha de treure del flux
        Thread.Sleep(150);
        Assert.False(player.FadingOut);
        left.Clear();
        Pump(player, left, 30); // després del fos de 100 ms

        Assert.Equal(0.2f, left[^1], 1e-4f);
        player.Dispose();
    }

    [Fact]
    public void Si_la_pista_s_acaba_a_mig_fade_out_no_encadena_amb_la_seguent()
    {
        var player = NewPlayer();
        var ids = player.Playlist.Add([Constant(0.4f, 0.5), Constant(0.2f, 2)]);
        player.Playlist.SetFlags(ids[0], autoNext: true);
        player.Play();
        Thread.Sleep(150);
        var left = new List<float>();
        player.FadeOut(5000);
        PumpUntilStopped(player, left);

        Assert.Equal(PlayerState.Ended, player.State);
        Assert.Equal(ids[0], player.LastPlayedId);   // la segona no ha sonat: Play la farà sonar
        Assert.True(left.Count < Rate, "s'atura quan s'acaba la primera (0,5 s), no a la segona");
        player.Dispose();
    }
}
