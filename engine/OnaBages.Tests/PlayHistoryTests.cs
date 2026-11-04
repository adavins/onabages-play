using System.Text;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;

namespace OnaBages.Tests;

/// <summary>Historial de reproducció (especificació 6.6): què compta com a «ha sonat» i com s'escriu el CSV.</summary>
public class PlayHistoryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "history-" + Guid.NewGuid().ToString("N"));
    private readonly PlayHistory _history = new();
    private readonly List<PlayedTrack> _finished = [];
    private DateTime _now = new(2026, 11, 7, 10, 0, 0);

    public PlayHistoryTests()
    {
        _history.Finished += t => _finished.Add(t);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static readonly TimeSpan TrackLength = TimeSpan.FromMinutes(3);

    /// <summary>Simula <paramref name="seconds"/> segons de revisions (10 per segon) amb el mateix estat.</summary>
    private void Run(double seconds, PlayerState state, string? item, string player = "1", string program = "Magazín")
    {
        int ticks = (int)Math.Round(seconds * 10);
        for (int i = 0; i < Math.Max(ticks, 1); i++)
        {
            _history.Observe(player, state, item, item is null ? null : $@"C:\musica\{item}.mp3", TrackLength, _now, program);
            _now = _now.AddMilliseconds(100);
        }
    }

    [Fact]
    public void Una_pista_que_acaba_queda_a_l_historial()
    {
        Run(30, PlayerState.Playing, "a");
        Run(0.1, PlayerState.Ended, null);

        var track = Assert.Single(_finished);
        Assert.Equal(@"C:\musica\a.mp3", track.FilePath);
        Assert.Equal("1", track.PlayerId);
        Assert.Equal("Magazín", track.Program);
        Assert.Equal(new DateTime(2026, 11, 7, 10, 0, 0), track.StartedAt);
        Assert.InRange(track.Played.TotalSeconds, 29.8, 30.2);
        Assert.Equal(TrackLength, track.Duration);
    }

    [Fact]
    public void Les_pauses_no_compten_com_a_temps_sonat()
    {
        Run(10, PlayerState.Playing, "a");
        Run(60, PlayerState.Paused, "a");
        Run(10, PlayerState.Playing, "a");
        Run(0.1, PlayerState.Ended, null);

        var track = Assert.Single(_finished);
        Assert.InRange(track.Played.TotalSeconds, 19.8, 20.2);
    }

    [Fact]
    public void Passar_a_una_altra_pista_tanca_l_anterior()
    {
        Run(10, PlayerState.Playing, "a");
        Run(5, PlayerState.Playing, "b");
        Run(0.1, PlayerState.Ended, null);

        Assert.Equal([@"C:\musica\a.mp3", @"C:\musica\b.mp3"], _finished.Select(t => t.FilePath));
        Assert.True(_finished[1].StartedAt > _finished[0].StartedAt);
    }

    [Fact]
    public void Un_play_i_stop_de_seguida_no_compta()
    {
        Run(1, PlayerState.Playing, "a");
        Run(0.1, PlayerState.Ended, null);

        Assert.Empty(_finished);
    }

    [Fact]
    public void Una_pista_preparada_que_no_sona_no_compta()
    {
        Run(30, PlayerState.Ready, "a");
        Run(0.1, PlayerState.Empty, null);

        Assert.Empty(_finished);
    }

    [Fact]
    public void Cada_reproductor_va_per_separat()
    {
        Run(10, PlayerState.Playing, "a", player: "1");
        Run(10, PlayerState.Playing, "b", player: "2");
        Run(0.1, PlayerState.Ended, null, player: "2");

        var track = Assert.Single(_finished);
        Assert.Equal("2", track.PlayerId); // el reproductor 1 continua sonant
    }

    [Fact]
    public void En_aturar_el_motor_el_que_sona_tambe_hi_va()
    {
        Run(10, PlayerState.Playing, "a");

        _history.FinishAll(_now);

        Assert.Single(_finished);
    }

    [Fact]
    public void El_programa_es_el_de_quan_comenca_la_pista()
    {
        Run(5, PlayerState.Playing, "a", program: "Matinal");
        Run(5, PlayerState.Playing, "a", program: "Esports");
        Run(0.1, PlayerState.Ended, null);

        Assert.Equal("Matinal", Assert.Single(_finished).Program);
    }

    // --- Fitxer CSV ---

    [Fact]
    public void El_csv_te_capcalera_bom_i_una_linia_per_pista()
    {
        var track = new PlayedTrack(new DateTime(2026, 11, 7, 10, 5, 3), "Magazín dels dissabtes", "2",
            @"D:\Música\Cançó.mp3", TimeSpan.FromSeconds(185), TimeSpan.FromSeconds(200));

        PlayHistoryFile.Append(track, "La cançó", "Grup; i amics", _folder);
        PlayHistoryFile.Append(track, null, null, _folder);

        string path = PlayHistoryFile.PathFor(track.StartedAt, _folder);
        Assert.EndsWith("historial-2026-11-07.csv", path);
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(Encoding.UTF8.Preamble.ToArray(), bytes[..3]); // l'Excel hi veu bé els accents
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        Assert.Equal(3, lines.Length);
        Assert.Equal(PlayHistoryFile.Header, lines[0]);
        Assert.Equal(@"2026-11-07;10:05:03;Magazín dels dissabtes;2;La cançó;""Grup; i amics"";D:\Música\Cançó.mp3;00:03:05;00:03:20", lines[1]);
        Assert.Equal(@"2026-11-07;10:05:03;Magazín dels dissabtes;2;;;D:\Música\Cançó.mp3;00:03:05;00:03:20", lines[2]);
    }

    [Theory]
    [InlineData("normal", "normal")]
    [InlineData("amb;punt i coma", "\"amb;punt i coma\"")]
    [InlineData("amb \"cometes\"", "\"amb \"\"cometes\"\"\"")]
    [InlineData("", "")]
    public void Els_camps_es_protegeixen_com_cal(string text, string expected)
    {
        Assert.Equal(expected, PlayHistoryFile.Field(text));
    }
}
