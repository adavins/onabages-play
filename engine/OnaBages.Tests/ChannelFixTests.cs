using System.Text.Json.Nodes;
using NAudio.Wave;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>
/// Pistes estèreo amb un canal buit (fase 5, pas 29a; D-132): l'anàlisi el detecta, el reproductor fa sonar el canal bo
/// pels dos (també si encadena, just on comença la pista), el botó ho canvia de seguida, la normalització ho compta, i
/// la tria es desa amb la llista.
/// </summary>
public class ChannelFixTests : IDisposable
{
    private const string DeviceId = "{0.0.0.00000000}.{proves}";
    private const int Rate = 48000;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "canals-" + Guid.NewGuid().ToString("N"));
    private FakeOutput? _output;

    public ChannelFixTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>WAV estèreo: un to de 1 kHz a <paramref name="leftDb"/> a l'esquerre i a <paramref name="rightDb"/> al dret (null = silenci).</summary>
    private string Wav(double? leftDb, double? rightDb, double seconds = 1, string? name = null)
    {
        string path = Path.Combine(_folder, (name ?? $"to-{Guid.NewGuid():N}") + ".wav");
        float l = leftDb is { } a ? (float)Math.Pow(10, a / 20) : 0f;
        float r = rightDb is { } b ? (float)Math.Pow(10, b / 20) : 0f;
        int frames = (int)(Rate * seconds);
        var samples = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            float sine = (float)Math.Sin(2 * Math.PI * 1000 * i / Rate);
            samples[i * 2] = l * sine;
            samples[i * 2 + 1] = r * sine;
        }
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2));
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private Player NewPlayer()
    {
        var player = new Player("1", _ => _output = new FakeOutput(), new TrackAnalyzer(synchronous: true));
        player.SetDevice(DeviceId);
        return player;
    }

    /// <summary>Fa de fil d'àudio uns quants blocs de 10 ms i en retorna els pics de cada canal.</summary>
    private (float Left, float Right) Pump(Player player, int blocks = 20)
    {
        float left = 0, right = 0;
        for (int n = 0; n < blocks; n++)
        {
            var block = _output!.Pump(480);
            for (int i = 0; i < block.Length; i += 2)
            {
                left = Math.Max(left, Math.Abs(block[i]));
                right = Math.Max(right, Math.Abs(block[i + 1]));
            }
            player.Tick();
            Thread.Sleep(1);
        }
        return (left, right);
    }

    // --- Detecció ---

    [Theory]
    [InlineData(-6.0, null, EmptyChannel.Right)]
    [InlineData(null, -6.0, EmptyChannel.Left)]
    [InlineData(-6.0, -6.0, EmptyChannel.None)]
    [InlineData(-6.0, -20.0, EmptyChannel.None)]   // més fluix, però amb so
    [InlineData(-30.0, -70.0, EmptyChannel.Right)] // un canal fluix (−30) i l'altre gairebé res (−70): buit
    [InlineData(-60.0, -95.0, EmptyChannel.None)]  // tots dos gairebé silenci: l'altre no té prou so (−60 no passa de −50 + 30)
    [InlineData(-10.0, -45.0, EmptyChannel.None)]  // soroll a −45: no és buit
    [InlineData(-10.0, -60.0, EmptyChannel.Right)] // −60, 50 dB per sota: buit
    [InlineData(null, null, EmptyChannel.None)] // silenci als dos
    public void Detecta_el_canal_buit(double? left, double? right, EmptyChannel expected)
    {
        Assert.Equal(expected, TrackLoudness.Measure(Wav(left, right)).EmptyChannel);
    }

    [Fact]
    public void Un_fitxer_mono_no_te_cap_canal_buit()
    {
        string path = Path.Combine(_folder, "mono.wav");
        var samples = Enumerable.Range(0, Rate).Select(i => 0.5f * (float)Math.Sin(2 * Math.PI * 440 * i / Rate)).ToArray();
        using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1)))
            writer.WriteSamples(samples, 0, samples.Length);
        Assert.Equal(EmptyChannel.None, TrackLoudness.Measure(path).EmptyChannel);
    }

    [Fact]
    public void La_memoria_cau_guarda_el_canal_buit_i_les_mesures_antigues_es_tornen_a_fer()
    {
        var cache = new LoudnessCacheFile(Path.Combine(_folder, "cache"));
        string path = Wav(-6, null);
        cache.Write(path, new TrackLoudness(-12, 0.5f, [], EmptyChannel.Right));
        Assert.Equal(EmptyChannel.Right, cache.Read(path)!.EmptyChannel);

        // Una mesura de la versió anterior (sense el canal) no es fa servir.
        var file = Directory.GetFiles(Path.Combine(_folder, "cache"), "*.lufs").Single();
        File.WriteAllText(file, "OBLU3;-12;0.5;");
        Assert.Null(cache.Read(path));
    }

    [Fact]
    public void Com_sona_segons_el_canal_buit_i_la_tria()
    {
        var item = new PlaylistItem("t1", "x", "x", null, null, TrackStatus.Ok, null, false, false);
        Assert.Equal(ChannelFix.None, item.ChannelFix);
        Assert.Equal(ChannelFix.Mix, (item with { BothChannels = true }).ChannelFix);
        Assert.Equal(ChannelFix.UseRight, (item with { EmptyChannel = EmptyChannel.Left }).ChannelFix);
        Assert.Equal(ChannelFix.UseLeft, (item with { EmptyChannel = EmptyChannel.Right }).ChannelFix);
        Assert.Equal(ChannelFix.UseLeft, (item with { EmptyChannel = EmptyChannel.Right, BothChannels = true }).ChannelFix);
        Assert.Equal(ChannelFix.None, (item with { EmptyChannel = EmptyChannel.Right, BothChannels = false }).ChannelFix);
    }

    // --- Reproductor ---

    [Fact]
    public void Una_pista_amb_un_canal_buit_sona_pels_dos()
    {
        using var player = NewPlayer();
        var id = player.Playlist.Add([Wav(-6, null, seconds: 3)])[0];
        Assert.Equal(EmptyChannel.Right, player.Playlist.Find(id)!.EmptyChannel);

        player.Play();
        Thread.Sleep(150);
        var (left, right) = Pump(player);
        Assert.Equal(0.5f, left, 0.01f);
        Assert.Equal(left, right, 0.001f);

        // El botó (desactivar-ho): el canal dret torna a ser buit de seguida.
        player.Playlist.SetFlags(id, bothChannels: false);
        Pump(player, 2);
        (left, right) = Pump(player);
        Assert.Equal(0.5f, left, 0.01f);
        Assert.Equal(0f, right);

        // I tornar-ho a activar.
        player.Playlist.SetFlags(id, bothChannels: true);
        Pump(player, 2);
        (_, right) = Pump(player);
        Assert.Equal(0.5f, right, 0.01f);
        player.Stop();
    }

    [Fact]
    public void El_boto_en_una_pista_normal_barreja_els_dos_canals()
    {
        using var player = NewPlayer();
        var id = player.Playlist.Add([Wav(-6, -12, seconds: 3)])[0];
        player.Playlist.SetFlags(id, bothChannels: true);
        player.Play();
        Thread.Sleep(150);
        var (left, right) = Pump(player);
        float expected = (0.501f + 0.251f) / 2;
        Assert.Equal(expected, left, 0.01f);
        Assert.Equal(left, right, 0.001f);
        player.Stop();
    }

    [Fact]
    public void En_encadenar_canvia_just_on_comenca_la_pista_seguent()
    {
        using var player = NewPlayer();
        // Primera: normal, amb so diferent a cada canal. Segona: només l'esquerre.
        var ids = player.Playlist.Add([Wav(-6, -20, seconds: 1.5), Wav(-6, null, seconds: 1.5)]);
        player.Playlist.SetFlags(ids[0], autoNext: true);
        player.Play();
        Thread.Sleep(150);

        var right = new List<float>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (player.State == PlayerState.Playing && DateTime.UtcNow < deadline)
        {
            var block = _output!.Pump(480);
            for (int i = 1; i < block.Length; i += 2) right.Add(block[i]);
            player.Tick();
            Thread.Sleep(1);
        }

        // Dret: −20 dB (0,1) a la primera; 0,5 (la còpia de l'esquerre) a la segona, des del primer fotograma.
        int boundary = (int)(Rate * 1.5);
        float before = right.Skip(boundary - 2400).Take(2400).Max(Math.Abs);
        float after = right.Skip(boundary).Take(2400).Max(Math.Abs);
        Assert.Equal(0.1f, before, 0.01f);
        Assert.Equal(0.5f, after, 0.01f);
        // El primer cicle de la segona ja és la còpia (no es fa tard fins a la revisió següent).
        Assert.True(right.Skip(boundary).Take(48).Max(Math.Abs) > 0.4f);
    }

    [Fact]
    public void La_normalitzacio_compta_que_sonara_3_dB_mes_fort()
    {
        using var player = NewPlayer();
        var empty = player.Playlist.Add([Wav(-20, null, seconds: 3)])[0];
        var both = player.Playlist.Add([Wav(-20, -20, seconds: 3)])[0];
        player.SetNormalization(true, -12);

        player.PlayItem(empty);
        double withFix = player.NormalizationDb!.Value;
        player.Stop();
        player.PlayItem(both);
        double normal = player.NormalizationDb!.Value;
        player.Stop();

        // La de dos canals mesura 3 dB més (dos canals amb so): amb la correcció, sonen igual i el guany és el mateix.
        Assert.Equal(normal, withFix, 0.2);
    }

    [Fact]
    public void El_fil_d_audio_no_crea_objectes_amb_la_correccio()
    {
        var ring = new SampleRingBuffer(Rate * 2);
        ring.Write(Enumerable.Repeat(0.3f, Rate * 2).ToArray());
        var stream = new PlayerStream(ring, Rate, 2) { ChannelFix = ChannelFix.UseLeft };
        var buffer = new byte[480 * 2 * sizeof(float)];
        for (int i = 0; i < 20; i++) stream.Read(buffer); // escalfar
        stream.ScheduleChannelFix(stream.FramesPlayed + 7000, ChannelFix.Mix);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 30; i++) stream.Read(buffer);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
        Assert.Equal(ChannelFix.Mix, stream.ChannelFix);
        Assert.False(stream.ChannelFixScheduled);
    }

    // --- Llistes i ordres ---

    [Fact]
    public void La_tria_es_desa_amb_la_llista_i_es_copia()
    {
        var playlist = new Playlist();
        var ids = playlist.Add([Wav(-6, -6, name: "a"), Wav(-6, -6, name: "b"), Wav(-6, -6, name: "c")]);
        playlist.SetFlags(ids[0], bothChannels: true);
        playlist.SetFlags(ids[1], bothChannels: false);
        string file = Path.Combine(_folder, "llista.obpl");
        PlaylistFiles.Write(file, playlist.Items);

        var loaded = PlaylistFiles.Read(file);
        Assert.Equal([true, false, null], loaded.Select(e => e.BothChannels));
        var copy = new Playlist();
        copy.AddEntries(loaded);
        Assert.Equal([true, false, null], copy.Items.Select(i => i.BothChannels));
        Assert.Equal([true], copy.Duplicate([copy.Items[0].Id]).Select(id => copy.Find(id)!.BothChannels));
    }

    [Fact]
    public void Ordre_setFlags_i_camps_de_la_pista()
    {
        using var player = NewPlayer();
        var id = player.Playlist.Add([Wav(null, -6)])[0];
        var router = new CommandRouter(new EngineState([player], []), () => { }, () => []);

        var json = PlaylistJson.Item(player.Playlist.Find(id)!);
        Assert.Equal("left", json["emptyChannel"]!.GetValue<string>());
        Assert.Null(json["bothChannels"]);
        Assert.Equal("right", json["channelFix"]!.GetValue<string>());

        var r = router.Handle($$"""{ "v": 1, "id": 1, "cmd": "playlist.setFlags", "player": "1", "itemId": "{{id}}", "bothChannels": false }""");
        Assert.True(r["ok"]!.GetValue<bool>(), r.ToJsonString());
        json = PlaylistJson.Item(player.Playlist.Find(id)!);
        Assert.False(json["bothChannels"]!.GetValue<bool>());
        Assert.Null(json["channelFix"]);

        var section = player.Playlist.AddSection("Secció");
        Assert.Throws<PlayerCommandException>(() => player.Playlist.SetFlags(section, bothChannels: true));
    }
}
