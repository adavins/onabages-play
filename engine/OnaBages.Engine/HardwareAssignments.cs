using OnaBages.Audio;
using OnaBages.Engine.Data;

namespace OnaBages.Engine;

/// <summary>
/// Recorda quin dispositiu té cada reproductor i enregistrador (especificació 5.6):
/// <list type="bullet">
/// <item>en arrencar, compara la configuració desada amb el maquinari i assigna el que es pot;</item>
/// <item>quan l'operador tria un dispositiu, el desa;</item>
/// <item>si un dispositiu que faltava torna, el torna a assignar al seu element (sense posar-lo a sonar).</item>
/// </list>
/// Mai no assigna un dispositiu diferent del que s'havia triat (D-016).
/// </summary>
public sealed class HardwareAssignments
{
    private readonly EngineState _state;
    private readonly Lock _sync = new();

    // Elements que esperen que torni el seu dispositiu.
    private readonly HashSet<(ElementKind Kind, string Id)> _waiting = [];

    public HardwareAssignments(EngineState state, ConfigStore store)
    {
        _state = state;
        Store = store;
    }

    /// <summary>Per a les proves: una configuració pròpia a partir d'un repositori.</summary>
    public HardwareAssignments(EngineState state, ConfigRepository repository)
        : this(state, new ConfigStore(repository))
    {
    }

    /// <summary>La configuració on es desen les assignacions.</summary>
    public ConfigStore Store { get; }

    /// <summary>Resum de l'arrencada (buit fins que es crida <see cref="Start"/>).</summary>
    public HardwareReport Report { get; private set; } = HardwareReport.Empty;

    /// <summary>Avisos d'un element que no s'ha pogut assignar.</summary>
    public event Action<string>? Warning;

    /// <summary>Llegeix la configuració, la compara amb el maquinari actual i assigna el que es pot.</summary>
    public HardwareReport Start(IReadOnlyList<AudioDevice> current)
    {
        var loaded = Store.Load();

        lock (_sync)
        {
            _waiting.Clear();
            var report = HardwareReport.Empty;

            Store.Update(config =>
            {
                // Sense cap assignació desada és com la primera arrencada: la interfície proposarà una assignació.
                bool firstRun = config.Players.Count == 0 && config.Recorders.Count == 0;
                report = HardwareCheck.Compare(config, Elements(), current, firstRun, loaded.Problem);

                foreach (var check in report.Elements)
                {
                    if (check.CanAssign)
                    {
                        if (Assign(check))
                            Remember(config, check);
                    }
                    else if (check.Result == CheckResult.Missing)
                    {
                        _waiting.Add((check.Kind, check.ElementId));
                    }
                    // Ambiguous: no s'assigna ni s'espera; l'operador n'ha de triar un.
                }

                config.KnownDevices = current.ToList();
            });

            Report = report;
            return report;
        }
    }

    /// <summary>L'operador ha triat un dispositiu per a un element: es desa.</summary>
    /// <param name="current">Dispositius actuals, per guardar-ne també el nom.</param>
    public void Remember(ElementKind kind, string elementId, string deviceId, IReadOnlyList<AudioDevice> current)
    {
        string name = current.FirstOrDefault(d => d.Id == deviceId)?.Name ?? "";
        lock (_sync)
        {
            _waiting.Remove((kind, elementId));
            Store.Update(config => HardwareCheck.Assignments(config, kind)[elementId] = new DeviceRef(deviceId, name));
        }
    }

    /// <summary>
    /// Han canviat els dispositius (connexió, desconnexió...). Si torna el dispositiu
    /// d'un element que l'esperava, s'hi torna a assignar.
    /// </summary>
    /// <returns>Els elements que s'han tornat a assignar.</returns>
    public IReadOnlyList<ElementCheck> DevicesChanged(IReadOnlyList<AudioDevice> current)
    {
        var reassigned = new List<ElementCheck>();
        lock (_sync)
        {
            Store.Update(config =>
            {
                foreach (var (kind, id) in _waiting.ToList())
                {
                    var saved = HardwareCheck.Assignments(config, kind).GetValueOrDefault(id);
                    if (saved is null)
                    {
                        _waiting.Remove((kind, id));
                        continue;
                    }

                    var (device, result) = HardwareCheck.Find(saved, HardwareCheck.DeviceKindOf(kind), current);
                    var check = new ElementCheck(kind, id, saved, device, result);
                    if (check.CanAssign && Assign(check))
                    {
                        Remember(config, check);
                        _waiting.Remove((kind, id));
                        reassigned.Add(check);
                    }
                }

                config.KnownDevices = current.ToList();
            });
        }
        return reassigned;
    }

    /// <summary>Elements que esperen el seu dispositiu (per a les proves i el registre).</summary>
    public bool IsWaiting(ElementKind kind, string elementId)
    {
        lock (_sync)
            return _waiting.Contains((kind, elementId));
    }

    private IEnumerable<(ElementKind, string)> Elements() =>
        _state.Players.Select(p => (ElementKind.Player, p.Id))
            .Concat(_state.Recorders.Select(r => (ElementKind.Recorder, r.Id)));

    /// <summary>Assigna el dispositiu trobat a l'element. Retorna false si l'element no l'accepta.</summary>
    private bool Assign(ElementCheck check)
    {
        var device = check.Device!;
        try
        {
            if (check.Kind == ElementKind.Player)
                _state.FindPlayer(check.ElementId)?.SetDevice(device.Id);
            else
                _state.FindRecorder(check.ElementId)?.SetDevice(device.Id);
            return true;
        }
        catch (Exception ex)
        {
            // Un element que no es deixa assignar no ha d'impedir els altres.
            Warning?.Invoke($"No s'ha pogut assignar «{device.Name}» a {ElementName(check.Kind, check.ElementId)}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Desa el dispositiu trobat (amb l'identificador i el nom d'ara).</summary>
    private static void Remember(EngineConfig config, ElementCheck check) =>
        HardwareCheck.Assignments(config, check.Kind)[check.ElementId] = new DeviceRef(check.Device!.Id, check.Device.Name);

    public static string ElementName(ElementKind kind, string id) =>
        kind == ElementKind.Player ? $"Reproductor {id}" : $"Enregistrador {id}";
}
