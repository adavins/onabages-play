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
    private readonly ConfigRepository _repository;
    private readonly Lock _sync = new();
    private EngineConfig _config = new();

    // Elements que esperen que torni el seu dispositiu.
    private readonly HashSet<(ElementKind Kind, string Id)> _waiting = [];

    public HardwareAssignments(EngineState state, ConfigRepository repository)
    {
        _state = state;
        _repository = repository;
    }

    /// <summary>Resum de l'arrencada (buit fins que es crida <see cref="Start"/>).</summary>
    public HardwareReport Report { get; private set; } = HardwareReport.Empty;

    /// <summary>Avisos que no són d'un element (p. ex. no s'ha pogut desar la configuració).</summary>
    public event Action<string>? Warning;

    /// <summary>Llegeix la configuració, la compara amb el maquinari actual i assigna el que es pot.</summary>
    public HardwareReport Start(IReadOnlyList<AudioDevice> current)
    {
        var loaded = _repository.Load();
        if (loaded.Problem is not null)
            Warning?.Invoke(loaded.Problem);

        lock (_sync)
        {
            _config = loaded.Config;
            _waiting.Clear();

            // Sense cap assignació desada és com la primera arrencada: la interfície proposarà una assignació.
            bool firstRun = _config.Players.Count == 0 && _config.Recorders.Count == 0;
            var report = HardwareCheck.Compare(_config, Elements(), current, firstRun, loaded.Problem);

            foreach (var check in report.Elements)
            {
                if (check.CanAssign)
                    Assign(check);
                else if (check.Result == CheckResult.Missing)
                    _waiting.Add((check.Kind, check.ElementId));
                // Ambiguous: no s'assigna ni s'espera; l'operador n'ha de triar un.
            }

            _config.KnownDevices = current.ToList();
            Report = report;
            SaveLocked();
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
            HardwareCheck.Assignments(_config, kind)[elementId] = new DeviceRef(deviceId, name);
            _waiting.Remove((kind, elementId));
            SaveLocked();
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
            foreach (var (kind, id) in _waiting.ToList())
            {
                var saved = HardwareCheck.Assignments(_config, kind).GetValueOrDefault(id);
                if (saved is null)
                {
                    _waiting.Remove((kind, id));
                    continue;
                }

                var (device, result) = HardwareCheck.Find(saved, HardwareCheck.DeviceKindOf(kind), current);
                var check = new ElementCheck(kind, id, saved, device, result);
                if (!check.CanAssign)
                    continue;

                if (Assign(check))
                {
                    _waiting.Remove((kind, id));
                    reassigned.Add(check);
                }
            }

            _config.KnownDevices = current.ToList();
            SaveLocked();
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

    /// <summary>Assigna el dispositiu trobat a l'element i actualitza la configuració (identificador i nom nous).</summary>
    private bool Assign(ElementCheck check)
    {
        var device = check.Device!;
        try
        {
            if (check.Kind == ElementKind.Player)
                _state.FindPlayer(check.ElementId)?.SetDevice(device.Id);
            else
                _state.FindRecorder(check.ElementId)?.SetDevice(device.Id);
        }
        catch (Exception ex)
        {
            // Un element que no es deixa assignar no ha d'impedir els altres.
            Warning?.Invoke($"No s'ha pogut assignar «{device.Name}» a {ElementName(check.Kind, check.ElementId)}: {ex.Message}");
            return false;
        }

        HardwareCheck.Assignments(_config, check.Kind)[check.ElementId] = new DeviceRef(device.Id, device.Name);
        return true;
    }

    private void SaveLocked()
    {
        try
        {
            _repository.Save(_config);
        }
        catch (Exception ex)
        {
            // Sense desar, el motor continua funcionant; només es perdria l'assignació en reiniciar.
            Warning?.Invoke($"No s'ha pogut desar la configuració ({_repository.FilePath}): {ex.Message}");
        }
    }

    public static string ElementName(ElementKind kind, string id) =>
        kind == ElementKind.Player ? $"Reproductor {id}" : $"Enregistrador {id}";
}
