using OnaBages.Audio;
using OnaBages.Engine.Data;

namespace OnaBages.Engine;

/// <summary>Tipus d'element que té un dispositiu assignat.</summary>
public enum ElementKind { Player, Recorder, Cue, Cart }

/// <summary>Què ha passat amb el dispositiu desat d'un element (especificació 5.6).</summary>
public enum CheckResult
{
    /// <summary>Hi és, amb el mateix identificador, i està actiu.</summary>
    Ok,
    /// <summary>Hi és però sense cable (p. ex. un jack de la Realtek). S'assigna igualment.</summary>
    Unplugged,
    /// <summary>Hi és però desactivat a Windows. S'assigna igualment.</summary>
    Disabled,
    /// <summary>Ha canviat d'identificador, però hi ha un sol dispositiu amb el mateix nom: és el mateix.</summary>
    IdChanged,
    /// <summary>Hi ha diversos dispositius amb el mateix nom: no se n'assigna cap.</summary>
    Ambiguous,
    /// <summary>No hi és: no se n'assigna cap.</summary>
    Missing,
}

/// <summary>Resultat d'un element: el dispositiu desat i el que s'hi ha trobat ara (o null).</summary>
public sealed record ElementCheck(ElementKind Kind, string ElementId, DeviceRef Saved, AudioDevice? Device, CheckResult Result)
{
    /// <summary>S'hi pot assignar el dispositiu trobat?</summary>
    public bool CanAssign => Device is not null && Result is not (CheckResult.Ambiguous or CheckResult.Missing);
}

/// <summary>Resum de l'arrencada: el que la interfície mostra a «Canvis en el maquinari».</summary>
public sealed record HardwareReport(
    bool FirstRun,
    string? ConfigProblem,
    IReadOnlyList<ElementCheck> Elements,
    IReadOnlyList<AudioDevice> NewDevices,
    IReadOnlyList<DeviceChange> FormatChanges)
{
    /// <summary>Hi ha res a explicar a l'operador? Si no, la interfície no mostra cap finestra.</summary>
    public bool HasChanges =>
        FirstRun || ConfigProblem is not null || NewDevices.Count > 0 || FormatChanges.Count > 0
        || Elements.Any(e => e.Result != CheckResult.Ok);

    public static HardwareReport Empty { get; } = new(false, null, [], [], []);
}

/// <summary>
/// Compara les assignacions desades amb els dispositius que hi ha ara.
/// No toca res: només calcula. Qui l'utilitza decideix què aplicar.
/// Regla principal (D-016): mai no es tria un dispositiu diferent; només el mateix
/// (mateix identificador) o un de sol amb el mateix nom.
/// </summary>
public static class HardwareCheck
{
    /// <summary>Busca el dispositiu desat entre els actuals.</summary>
    /// <param name="kind">Sortida per als reproductors, entrada per als enregistradors.</param>
    public static (AudioDevice? Device, CheckResult Result) Find(DeviceRef saved, DeviceKind kind, IReadOnlyList<AudioDevice> current)
    {
        var sameId = current.FirstOrDefault(d => d.Id == saved.Id && d.Kind == kind);
        if (sameId is not null)
        {
            var result = sameId.Status switch
            {
                DeviceStatus.Active => CheckResult.Ok,
                DeviceStatus.Unplugged => CheckResult.Unplugged,
                _ => CheckResult.Disabled,
            };
            return (sameId, result);
        }

        if (string.IsNullOrEmpty(saved.Name))
            return (null, CheckResult.Missing);

        var sameName = current.Where(d => d.Kind == kind && d.Name == saved.Name).ToList();
        return sameName.Count switch
        {
            0 => (null, CheckResult.Missing),
            1 => (sameName[0], CheckResult.IdChanged),
            _ => (null, CheckResult.Ambiguous),
        };
    }

    /// <summary>Resum complet de l'arrencada.</summary>
    /// <param name="elements">Elements que existeixen al motor (tipus i identificador).</param>
    public static HardwareReport Compare(
        EngineConfig config,
        IEnumerable<(ElementKind Kind, string Id)> elements,
        IReadOnlyList<AudioDevice> current,
        bool firstRun,
        string? configProblem)
    {
        var checks = new List<ElementCheck>();
        foreach (var (kind, id) in elements)
        {
            var saved = Assignments(config, kind).GetValueOrDefault(id);
            if (saved is null)
                continue; // mai no se n'ha triat cap: no és un canvi

            var (device, result) = Find(saved, DeviceKindOf(kind), current);
            checks.Add(new ElementCheck(kind, id, saved, device, result));
        }

        // A la primera arrencada no se sap què és nou: no es diu res dels dispositius.
        var newDevices = new List<AudioDevice>();
        var formatChanges = new List<DeviceChange>();
        if (!firstRun && config.KnownDevices.Count > 0)
        {
            var assignedIds = checks.Where(c => c.Device is not null).Select(c => c.Device!.Id).ToHashSet();
            foreach (var change in DeviceChanges.Compare(config.KnownDevices, current))
            {
                if (change.Type == DeviceChangeType.Added && change.Device.Status == DeviceStatus.Active && !assignedIds.Contains(change.Device.Id))
                    newDevices.Add(change.Device);
                else if (change.Type == DeviceChangeType.FormatChanged && change.Device.Format is not null && change.PreviousFormat is not null)
                    formatChanges.Add(change);
            }
        }

        return new HardwareReport(firstRun, configProblem, checks, newDevices, formatChanges);
    }

    public static Dictionary<string, DeviceRef> Assignments(EngineConfig config, ElementKind kind) => kind switch
    {
        ElementKind.Player => config.Players,
        ElementKind.Recorder => config.Recorders,
        ElementKind.Cart => config.CartDevice,
        _ => config.Cue,
    };

    /// <summary>Els reproductors i la preescolta fan servir sortides; els enregistradors, entrades.</summary>
    public static DeviceKind DeviceKindOf(ElementKind kind) =>
        kind == ElementKind.Recorder ? DeviceKind.Input : DeviceKind.Output;
}
