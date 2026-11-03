using OnaBages.Audio;

namespace OnaBages.Tests;

/// <summary>Màquina d'estats del reproductor, amb una sortida falsa (sense targeta de so).</summary>
public class PlayerTests
{
    private const string DeviceId = "{0.0.0.00000000}.{proves}";

    private FakeOutput? _lastOutput;

    private Player NewPlayer(Func<string, IAudioOutput>? openOutput = null) =>
        new("1", openOutput ?? (_ => _lastOutput = new FakeOutput()));

    private Player ReadyPlayer(double seconds = 1.0)
    {
        var player = NewPlayer();
        player.SetDevice(DeviceId);
        player.Load(TestAudio.CreateWav(48000, 2, seconds));
        return player;
    }

    /// <summary>Fa de fil d'àudio fins al final de la pista. Retorna les mostres per canal enviades.</summary>
    private long PumpUntilEnd(Player player)
    {
        Thread.Sleep(200); // deixa que el fil de lectura ompli la memòria
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            _lastOutput!.Pump(480);
            player.Tick();
            if (player.State != PlayerState.Playing) break;
        }
        return (long)(player.Position.TotalSeconds * 48000);
    }

    [Fact]
    public void Sense_sortida_no_es_pot_carregar()
    {
        using var player = NewPlayer();

        var ex = Assert.Throws<PlayerCommandException>(() => player.Load(TestAudio.CreateWav(48000, 2, 0.1)));
        Assert.Contains("sortida", ex.Message);
        Assert.Equal(PlayerState.Empty, player.State);
    }

    [Fact]
    public void Carregar_deixa_el_reproductor_preparat_amb_la_durada()
    {
        using var player = ReadyPlayer(1.0);

        Assert.Equal(PlayerState.Ready, player.State);
        Assert.Equal(TimeSpan.FromSeconds(1), player.Duration);
        Assert.Equal(TimeSpan.Zero, player.Position);
    }

    [Fact]
    public void Sona_fins_al_final_sense_perdre_cap_mostra_ni_talls()
    {
        using var player = ReadyPlayer(1.0);

        player.Play();
        Assert.Equal(PlayerState.Playing, player.State);
        Assert.True(_lastOutput!.IsPlaying);

        long frames = PumpUntilEnd(player);

        Assert.Equal(PlayerState.Ended, player.State);
        Assert.Equal(48000, frames);
        Assert.Equal(0, player.Underruns);
    }

    [Fact]
    public void Play_just_despres_de_carregar_no_fa_talls()
    {
        using var player = ReadyPlayer(1.0);

        player.Play();             // sense esperar gens després de Load
        _lastOutput!.Pump(4800);   // la targeta demana dades immediatament

        Assert.Equal(0, player.Underruns);
    }

    [Fact]
    public void Pausa_i_continuar()
    {
        using var player = ReadyPlayer();
        player.Play();
        Thread.Sleep(100);
        _lastOutput!.Pump(4800);

        player.Pause();
        Assert.Equal(PlayerState.Paused, player.State);
        Assert.False(_lastOutput.IsPlaying);
        var position = player.Position;
        Assert.Equal(0.1, position.TotalSeconds, 3);

        player.Play();
        Assert.Equal(PlayerState.Playing, player.State);
        Assert.Equal(position, player.Position);
    }

    [Fact]
    public void Stop_torna_a_l_inici()
    {
        using var player = ReadyPlayer();
        player.Play();
        Thread.Sleep(100);
        _lastOutput!.Pump(4800);

        player.Stop();

        Assert.Equal(PlayerState.Ready, player.State);
        Assert.Equal(TimeSpan.Zero, player.Position);
    }

    [Fact]
    public void Play_despres_del_final_torna_a_comencar()
    {
        using var player = ReadyPlayer(0.2);
        player.Play();
        PumpUntilEnd(player);
        Assert.Equal(PlayerState.Ended, player.State);

        player.Play();

        Assert.Equal(PlayerState.Playing, player.State);
        Assert.Equal(TimeSpan.Zero, player.Position);
    }

    [Fact]
    public void Dispositiu_desconnectat_passa_a_error_i_conserva_pista_i_posicio()
    {
        using var player = ReadyPlayer();
        string file = player.FilePath!;
        player.Play();
        Thread.Sleep(100);
        _lastOutput!.Pump(4800);

        _lastOutput.Fail("dispositiu desconnectat");
        player.Tick();

        Assert.Equal(PlayerState.Error, player.State);
        Assert.Contains("Dispositiu no disponible", player.ErrorMessage);
        Assert.DoesNotContain("dispositiu desconnectat", player.ErrorMessage); // el text tècnic no va a l'operador...
        Assert.Equal("dispositiu desconnectat", player.ErrorDetail);            // ...sinó al detall
        Assert.Equal(file, player.FilePath);
        Assert.Equal(0.1, player.Position.TotalSeconds, 3);
        Assert.Throws<PlayerCommandException>(() => player.Play()); // no reprèn sol

        // Recuperació: tornar a carregar.
        player.Load(file);
        Assert.Equal(PlayerState.Ready, player.State);
        Assert.Null(player.ErrorMessage);
    }

    [Fact]
    public void Reintentar_continua_des_de_la_mateixa_posicio()
    {
        using var player = ReadyPlayer(1.0);
        player.Play();
        Thread.Sleep(100);
        _lastOutput!.Pump(12000); // 0,25 s
        _lastOutput.Fail("dispositiu desconnectat");
        player.Tick();
        Assert.Equal(PlayerState.Error, player.State);

        player.Retry();

        Assert.Equal(PlayerState.Ready, player.State);
        Assert.Equal(0.25, player.Position.TotalSeconds, 3);
        Assert.Null(player.ErrorMessage);

        // Continua des d'aquí fins al final: el que queda són 0,75 s.
        player.Play();
        long framesAfterRetry = 0;
        Thread.Sleep(100);
        while (player.State == PlayerState.Playing)
        {
            _lastOutput!.Pump(480);
            framesAfterRetry += 480;
            player.Tick();
        }
        Assert.Equal(PlayerState.Ended, player.State);
        Assert.Equal(TimeSpan.FromSeconds(1), player.Position);
        // 0,75 s, més el silenci final perquè soni tot (200 ms), més els blocs de l'última volta.
        Assert.InRange(framesAfterRetry, 36000 + 9600, 36000 + 9600 + 960);
    }

    [Fact]
    public void Reintentar_sense_error_no_es_permet()
    {
        using var player = ReadyPlayer();

        Assert.Throws<PlayerCommandException>(() => player.Retry());
    }

    [Fact]
    public void Canviar_de_sortida_despres_d_un_error_conserva_la_posicio()
    {
        using var player = ReadyPlayer(1.0);
        player.Play();
        Thread.Sleep(100);
        _lastOutput!.Pump(24000); // 0,5 s
        _lastOutput.Fail("dispositiu desconnectat");
        player.Tick();

        player.SetDevice("{0.0.0.00000000}.{una-altra}");

        Assert.Equal(PlayerState.Ready, player.State);
        Assert.Equal(0.5, player.Position.TotalSeconds, 3);
    }

    [Fact]
    public void Dispositiu_que_no_es_pot_obrir_deixa_error()
    {
        using var player = NewPlayer(_ => throw new AudioDeviceException("Dispositiu no disponible: no es troba a Windows."));
        player.SetDevice(DeviceId);
        player.Load(TestAudio.CreateWav(48000, 2, 0.1));

        Assert.Equal(PlayerState.Error, player.State);
        Assert.Contains("no es troba", player.ErrorMessage);
    }

    [Fact]
    public void Fitxer_illegible_deixa_error_amb_el_motiu()
    {
        using var player = NewPlayer();
        player.SetDevice(DeviceId);

        player.Load(@"C:\no\existeix.mp3");

        Assert.Equal(PlayerState.Error, player.State);
        Assert.Contains("No es troba", player.ErrorMessage);
    }

    [Fact]
    public void No_es_pot_carregar_ni_canviar_sortida_mentre_sona()
    {
        using var player = ReadyPlayer();
        player.Play();

        Assert.Throws<PlayerCommandException>(() => player.Load(player.FilePath!));
        Assert.Throws<PlayerCommandException>(() => player.SetDevice("altre"));
        Assert.Equal(PlayerState.Playing, player.State);
    }

    [Fact]
    public void Cada_canvi_d_estat_s_avisa()
    {
        using var player = NewPlayer();
        var states = new List<PlayerState>();
        player.StateChanged += p => states.Add(p.State);

        player.SetDevice(DeviceId);
        player.Load(TestAudio.CreateWav(48000, 2, 0.1));
        player.Play();
        player.Pause();
        player.Stop();

        Assert.Equal(
            [PlayerState.Empty, PlayerState.Ready, PlayerState.Playing, PlayerState.Paused, PlayerState.Ready],
            states);
    }

    [Fact]
    public void Saltar_mentre_sona_continua_sonant_des_d_alla()
    {
        using var player = ReadyPlayer(1.0);
        player.Play();
        Thread.Sleep(100);
        _lastOutput!.Pump(4800);

        player.Seek(TimeSpan.FromSeconds(0.5));

        Assert.Equal(PlayerState.Playing, player.State);
        Assert.Equal(0.5, player.Position.TotalSeconds, 3);
        long frames = PumpUntilEnd(player);
        Assert.Equal(PlayerState.Ended, player.State);
        Assert.Equal(48000, frames); // acaba al final de la pista: n'ha sonat la segona meitat
        Assert.Equal(0, player.Underruns);
    }

    [Fact]
    public void Saltar_en_pausa_queda_en_pausa_a_la_posicio_nova()
    {
        using var player = ReadyPlayer(1.0);
        player.Play();
        player.Pause();

        player.Seek(TimeSpan.FromSeconds(0.3));

        Assert.Equal(PlayerState.Paused, player.State);
        Assert.Equal(0.3, player.Position.TotalSeconds, 3);
    }

    [Fact]
    public void Tornar_a_comencar_es_saltar_a_zero()
    {
        using var player = ReadyPlayer(1.0);
        player.Play();
        Thread.Sleep(100);
        _lastOutput!.Pump(24000);

        player.Seek(TimeSpan.Zero);

        Assert.Equal(PlayerState.Playing, player.State);
        Assert.Equal(TimeSpan.Zero, player.Position);
    }

    [Fact]
    public void Saltar_mes_enlla_del_final_es_queda_al_final()
    {
        using var player = ReadyPlayer(1.0);

        player.Seek(TimeSpan.FromSeconds(99));

        Assert.Equal(TimeSpan.FromSeconds(1), player.Position);
    }

    [Fact]
    public void Sense_pista_no_es_pot_saltar()
    {
        using var player = NewPlayer();
        player.SetDevice(DeviceId);

        Assert.Throws<PlayerCommandException>(() => player.Seek(TimeSpan.FromSeconds(1)));
    }
}
