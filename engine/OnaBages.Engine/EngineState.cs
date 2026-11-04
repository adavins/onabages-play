using System.Diagnostics;
using System.Text.Json.Nodes;
using OnaBages.Audio;

namespace OnaBages.Engine;

/// <summary>
/// Estat global del motor. El motor és la font de veritat: la interfície
/// el demana amb <c>state.get</c> en connectar-se.
/// Fase 1: reproductors i enregistradors fixos; el dispositiu de cada un es desa a config.json.
/// </summary>
public sealed class EngineState
{
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private int _clients;

    /// <param name="players">Reproductors del motor. Les proves en poden passar de falsos, o cap.</param>
    /// <param name="recorders">Enregistradors del motor. Igual.</param>
    /// <param name="cue">Preescolta. Les proves en poden passar una amb una sortida falsa.</param>
    /// <param name="editor">Editor. Les proves en passen un amb la memòria cau en una carpeta temporal.</param>
    public EngineState(IEnumerable<Player>? players = null, IEnumerable<Recorder>? recorders = null, CuePlayer? cue = null,
        EditorSession? editor = null)
    {
        Editor = editor ?? new EditorSession();
        Players = (players ?? []).ToList();
        Recorders = (recorders ?? []).ToList();
        Cue = cue ?? new CuePlayer();
    }

    /// <summary>Identificador de la preescolta a config.json i als avisos de maquinari (només n'hi ha una).</summary>
    public const string CueId = "1";

    /// <summary>Preescolta (especificació 7): una sola, amb sortida pròpia.</summary>
    public CuePlayer Cue { get; }

    /// <summary>
    /// La preescolta surt per la mateixa sortida que algun reproductor (D-015): el que s'hi escolti
    /// sortirà a antena. Es permet, però la interfície ho ha de dir clarament.
    /// </summary>
    public bool CueSharesPlayerOutput =>
        Cue.DeviceId is { } id && Players.Any(p => p.DeviceId == id);

    /// <summary>Enregistradors, en ordre ("1", "2"...).</summary>
    public IReadOnlyList<Recorder> Recorders { get; }

    /// <summary>Enregistrador amb aquest identificador, o null si no existeix.</summary>
    public Recorder? FindRecorder(string? id) => Recorders.FirstOrDefault(r => r.Id == id);

    /// <summary>Hi ha algun enregistrament en curs?</summary>
    public bool IsRecording => Recorders.Any(r => r.State == RecorderState.Recording);

    /// <summary>Reproductors, en ordre ("1", "2"...).</summary>
    public IReadOnlyList<Player> Players { get; }

    /// <summary>Reproductor amb aquest identificador, o null si no existeix.</summary>
    public Player? FindPlayer(string? id) => Players.FirstOrDefault(p => p.Id == id);

    /// <summary>Editor (fase 3): un sol fitxer obert alhora.</summary>
    public EditorSession Editor { get; }

    public double UptimeSeconds => Math.Round(_uptime.Elapsed.TotalSeconds, 1);

    public int Clients => Volatile.Read(ref _clients);

    public void SetClients(int count) => Volatile.Write(ref _clients, count);

    public JsonObject ToJson() => new()
    {
        ["version"] = EngineInfo.Version,
        ["uptime"] = UptimeSeconds,
        ["clients"] = Clients,
    };
}
