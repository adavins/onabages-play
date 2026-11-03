using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>Llistes de reproducció (pas 4 de la fase 2, D-031): regles, avanç i ordres.</summary>
public class PlaylistTests
{
    private const string DeviceId = "{0.0.0.00000000}.{proves}";

    private FakeOutput? _output;

    private static string Wav(double seconds = 1.0) => TestAudio.CreateWav(48000, 2, seconds);

    private Player NewPlayer()
    {
        var player = new Player("1", _ => _output = new FakeOutput(), new TrackAnalyzer(synchronous: true));
        player.SetDevice(DeviceId);
        return player;
    }

    /// <summary>Fa de fil d'àudio fins que el reproductor s'atura. Retorna tot el que ha sortit (estèreo).</summary>
    private List<float> PumpUntilStopped(Player player, double maxSeconds = 10)
    {
        Thread.Sleep(150);
        var all = new List<float>();
        var deadline = DateTime.UtcNow.AddSeconds(maxSeconds);
        while (player.State == PlayerState.Playing && DateTime.UtcNow < deadline)
        {
            all.AddRange(_output!.Pump(480));
            player.Tick();
            Thread.Sleep(1); // deixa treballar el fil de lectura
        }
        return all;
    }

    /// <summary>El tros de silenci més llarg (en mostres per canal), sense comptar el del final.</summary>
    private static int LongestGap(List<float> stereo)
    {
        int longest = 0, run = 0;
        int lastSound = stereo.FindLastIndex(v => v != 0f) / 2;
        for (int i = 0; i < lastSound; i++)
        {
            run = stereo[i * 2] == 0f ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        return longest;
    }

    // --- Regles de la llista ---

    [Fact]
    public void Afegir_al_final_o_davant_d_una_pista()
    {
        var list = new Playlist();
        var first = list.Add([@"C:\a\Un.mp3", @"C:\a\Dos.mp3"]);
        var middle = list.Add([@"C:\a\Mig.wav"], beforeId: first[1]);

        Assert.Equal(["Un", "Mig", "Dos"], list.Items.Select(i => i.Name));
        Assert.Equal(3, list.Items.Select(i => i.Id).Distinct().Count());
        Assert.Equal(TrackStatus.Analyzing, list.Find(middle[0])!.Status);
    }

    [Fact]
    public void Moure_diverses_pistes_mante_el_seu_ordre()
    {
        var list = new Playlist();
        var ids = list.Add(new[] { "A", "B", "C", "D", "E" }.Select(n => $@"C:\{n}.mp3"));

        list.Move([ids[3], ids[1]], beforeId: ids[0]);   // B i D al principi
        Assert.Equal(["B", "D", "A", "C", "E"], list.Items.Select(i => i.Name));

        list.Move([ids[0]], beforeId: null);              // A al final
        Assert.Equal(["B", "D", "C", "E", "A"], list.Items.Select(i => i.Name));

        list.Move([ids[2], ids[4]], beforeId: ids[2]);    // destí dins del que es mou: davant de la següent
        Assert.Equal(["B", "D", "C", "E", "A"], list.Items.Select(i => i.Name));
    }

    [Fact]
    public void La_seguent_es_calcula_per_identitat_i_si_s_ha_esborrat_per_posicio()
    {
        var list = new Playlist();
        var ids = list.Add(new[] { "A", "B", "C" }.Select(n => $@"C:\{n}.mp3"));

        Assert.Equal(ids[0], list.NextAfter(null));
        Assert.Equal(ids[2], list.NextAfter(ids[1]));
        Assert.Null(list.NextAfter(ids[2]));

        list.Move([ids[2]], beforeId: ids[1]);            // A C B
        Assert.Equal(ids[1], list.NextAfter(ids[2]));

        list.Remove([ids[2]]);                            // s'esborra C: la següent és la que ocupava el seu lloc
        Assert.Equal(ids[1], list.NextAfter(ids[2]));
    }

    [Fact]
    public void Nomes_hi_ha_un_cue_i_s_apaga_en_sonar_o_en_esborrar_la_pista()
    {
        var list = new Playlist();
        var ids = list.Add(new[] { "A", "B", "C" }.Select(n => $@"C:\{n}.mp3"));

        list.SetFlags(ids[1], cue: true);
        list.SetFlags(ids[2], cue: true);
        Assert.Equal(ids[2], list.CueId);

        list.ClearCueIf(ids[1]);                          // no és la del cue: no canvia res
        Assert.Equal(ids[2], list.CueId);
        list.ClearCueIf(ids[2]);
        Assert.Null(list.CueId);

        list.SetFlags(ids[0], cue: true, loop: true);
        list.Remove([ids[0]]);
        Assert.Null(list.CueId);
    }

    [Fact]
    public void La_comprovacio_dona_durada_format_i_la_durada_total()
    {
        var list = new Playlist(new TrackAnalyzer(synchronous: true));
        var ids = list.Add([Wav(1.0), Wav(2.5)]);

        var item = list.Find(ids[0])!;
        Assert.Equal(TrackStatus.Ok, item.Status);
        Assert.Equal(TimeSpan.FromSeconds(1), item.Duration);
        Assert.Equal("WAV 32 bits estèreo 48 kHz", item.Format);
        Assert.Equal(TimeSpan.FromSeconds(3.5), list.TotalDuration);
    }

    [Fact]
    public void Un_fitxer_malmes_es_marca_amb_el_motiu()
    {
        string path = Path.Combine(Path.GetTempPath(), "OnaBagesTests", $"malmes-{Guid.NewGuid():N}.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "això no és un mp3");
        var list = new Playlist(new TrackAnalyzer(synchronous: true));

        var item = list.Find(list.Add([path])[0])!;

        Assert.Equal(TrackStatus.Error, item.Status);
        Assert.NotNull(item.Problem);
        Assert.Equal("malmes-", item.Name[..7]); // el nom es mostra igualment
    }

    [Fact]
    public void Un_fitxer_que_desapareix_es_marca_i_si_torna_es_torna_a_comprovar()
    {
        string path = Wav();
        var list = new Playlist(new TrackAnalyzer(synchronous: true));
        string id = list.Add([path])[0];
        string moved = path + ".amagat";

        File.Move(path, moved);
        list.CheckFiles();
        Assert.Equal(TrackStatus.Missing, list.Find(id)!.Status);
        Assert.Contains("ja no hi és", list.Find(id)!.Problem);

        File.Move(moved, path);
        list.CheckFiles();
        Assert.Equal(TrackStatus.Ok, list.Find(id)!.Status);
    }

    // --- Avanç ---

    [Fact]
    public void Avanc_manual_play_fa_sonar_la_seguent()
    {
        using var player = NewPlayer();
        var ids = player.Playlist.Add([Wav(0.3), Wav(0.3)]);

        player.Play();                                    // sense res sonat: la primera
        Assert.Equal(ids[0], player.CurrentItemId);
        PumpUntilStopped(player);

        Assert.Equal(PlayerState.Ended, player.State);    // per defecte, s'atura
        Assert.Equal(ids[0], player.LastPlayedId);

        player.Play();
        Assert.Equal(ids[1], player.CurrentItemId);
    }

    [Fact]
    public void Play_fa_sonar_la_del_cue_i_el_cue_s_apaga()
    {
        using var player = NewPlayer();
        var ids = player.Playlist.Add([Wav(0.3), Wav(0.3), Wav(0.3)]);
        player.Playlist.SetFlags(ids[2], cue: true);

        player.Play();

        Assert.Equal(ids[2], player.CurrentItemId);
        Assert.Null(player.Playlist.CueId);
    }

    [Fact]
    public void Doble_clic_canvia_de_pista_i_l_anterior_queda_com_a_ultima()
    {
        using var player = NewPlayer();
        var ids = player.Playlist.Add([Wav(), Wav()]);
        player.Play();

        player.PlayItem(ids[1]);

        Assert.Equal(PlayerState.Playing, player.State);
        Assert.Equal(ids[1], player.CurrentItemId);
        Assert.Equal(ids[0], player.LastPlayedId);
        Assert.Equal(TimeSpan.Zero, player.Position);
    }

    [Fact]
    public void Avanc_automatic_encadena_la_seguent_sense_silenci_i_nomes_un_pas()
    {
        using var player = NewPlayer();
        var ids = player.Playlist.Add([Wav(0.5), Wav(0.5), Wav(0.5)]);
        player.Playlist.SetFlags(ids[0], autoNext: true);

        player.Play();
        var output = PumpUntilStopped(player);

        Assert.Equal(PlayerState.Ended, player.State);
        Assert.Equal(ids[1], player.LastPlayedId);        // ha sonat la 2a i s'ha aturat (un sol pas)
        Assert.True(LongestGap(output) < 10, $"Silenci entre pistes: {LongestGap(output)} mostres");
        Assert.Equal(0, player.Underruns);
    }

    [Fact]
    public void Avanc_automatic_va_a_la_del_cue_si_n_hi_ha()
    {
        using var player = NewPlayer();
        var ids = player.Playlist.Add([Wav(0.5), Wav(0.5), Wav(0.5)]);
        player.Playlist.SetFlags(ids[0], autoNext: true);
        player.Playlist.SetFlags(ids[2], cue: true);

        player.Play();
        PumpUntilStopped(player);

        Assert.Equal(ids[2], player.LastPlayedId);
        Assert.Null(player.Playlist.CueId);
    }

    [Fact]
    public void Bucle_repeteix_la_pista_fins_que_s_atura()
    {
        using var player = NewPlayer();
        var ids = player.Playlist.Add([Wav(0.3), Wav(0.3)]);
        player.Playlist.SetFlags(ids[0], loop: true);
        player.Play();
        Thread.Sleep(150);

        // 1 s de so: més de tres voltes d'una pista de 0,3 s.
        var output = new List<float>();
        for (int i = 0; i < 100; i++)
        {
            output.AddRange(_output!.Pump(480));
            player.Tick();
            Thread.Sleep(1);
        }

        Assert.Equal(PlayerState.Playing, player.State);
        Assert.Equal(ids[0], player.CurrentItemId);
        Assert.True(player.Position < TimeSpan.FromSeconds(0.3));
        Assert.True(LongestGap(output) < 10);

        player.Stop();
        Assert.Equal(ids[0], player.LastPlayedId);
    }

    [Fact]
    public void Esborrar_la_pista_que_sona_no_l_atura()
    {
        using var player = NewPlayer();
        var ids = player.Playlist.Add([Wav(), Wav()]);
        player.Play();

        player.Playlist.Remove([ids[0]]);
        player.Tick();

        Assert.Equal(PlayerState.Playing, player.State);
        Assert.Equal(ids[0], player.CurrentItemId);
    }

    [Fact]
    public void Una_pista_que_no_es_pot_obrir_es_marca_a_la_llista()
    {
        using var player = NewPlayer();
        var ids = player.Playlist.Add([@"C:\no\existeix.mp3"]);

        player.Play();

        Assert.Equal(PlayerState.Error, player.State);
        Assert.Equal(TrackStatus.Error, player.Playlist.Find(ids[0])!.Status);
    }

    // --- Ordres ---

    [Fact]
    public void Ordres_de_la_llista()
    {
        var state = new EngineState([new Player("1", _ => new FakeOutput(), new TrackAnalyzer(synchronous: true))]);
        var router = new CommandRouter(state, () => { }, () => []);
        JsonObject Send(string json) => router.Handle(json);
        string a = Wav().Replace("\\", "\\\\"), b = Wav(2).Replace("\\", "\\\\");

        var r = Send($$"""{ "v": 1, "id": 1, "cmd": "playlist.add", "player": "1", "paths": ["{{a}}", "{{b}}"] }""");
        var ids = r["result"]!["ids"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(2, ids.Count);

        r = Send($$"""{ "v": 1, "id": 2, "cmd": "playlist.move", "player": "1", "ids": ["{{ids[1]}}"], "beforeId": "{{ids[0]}}" }""");
        var items = r["result"]!["playlist"]!["items"]!.AsArray();
        Assert.Equal(ids[1], items[0]!["id"]!.GetValue<string>());
        Assert.Equal(3.0, r["result"]!["playlist"]!["totalDuration"]!.GetValue<double>());
        Assert.Equal("ok", items[0]!["status"]!.GetValue<string>());

        r = Send($$"""{ "v": 1, "id": 3, "cmd": "playlist.setFlags", "player": "1", "itemId": "{{ids[0]}}", "loop": true, "cue": true }""");
        Assert.Equal(ids[0], r["result"]!["playlist"]!["cueId"]!.GetValue<string>());

        Assert.Equal("bad_params", Send("""{ "v": 1, "id": 4, "cmd": "playlist.setFlags", "player": "1", "itemId": "no" }""")["error"]!["code"]!.GetValue<string>());
        Assert.Equal("bad_params", Send("""{ "v": 1, "id": 5, "cmd": "playlist.add", "player": "1", "paths": ["relatiu.mp3"] }""")["error"]!["code"]!.GetValue<string>());

        Send($$"""{ "v": 1, "id": 6, "cmd": "player.setDevice", "player": "1", "deviceId": "{{DeviceId}}" }""");
        r = Send($$"""{ "v": 1, "id": 7, "cmd": "player.playItem", "player": "1", "itemId": "{{ids[1]}}" }""");
        Assert.Equal("playing", r["result"]!["player"]!["state"]!.GetValue<string>());
        Assert.Equal(ids[1], r["result"]!["player"]!["currentItemId"]!.GetValue<string>());
        Assert.Equal("WAV 32 bits estèreo 48 kHz", r["result"]!["player"]!["format"]!.GetValue<string>());

        r = Send($$"""{ "v": 1, "id": 8, "cmd": "playlist.remove", "player": "1", "ids": ["{{ids[0]}}"] }""");
        Assert.Single(r["result"]!["playlist"]!["items"]!.AsArray());

        var stateGet = Send("""{ "v": 1, "id": 9, "cmd": "state.get" }""");
        Assert.Equal("1", stateGet["result"]!["playlists"]![0]!["player"]!.GetValue<string>());

        r = Send($$"""{ "v": 1, "id": 10, "cmd": "track.info", "path": "{{b}}" }""");
        Assert.Equal(2.0, r["result"]!["info"]!["duration"]!.GetValue<double>());
        Assert.Null(r["result"]!["info"]!["title"]);
    }
}
