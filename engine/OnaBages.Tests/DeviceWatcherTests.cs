using OnaBages.Audio;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

public class DeviceWatcherTests
{
    private static readonly DeviceFormat Hz48 = new(48000, 24, 2);

    private static AudioDevice Antena(DeviceStatus status = DeviceStatus.Active, string name = "Antena 1", DeviceFormat? format = null) =>
        new("{0.0.0.00000000}.{a}", name, DeviceKind.Output, status, format ?? Hz48);

    private static AudioDevice Usb() =>
        new("{0.0.0.00000000}.{usb}", "Taula USB", DeviceKind.Output, DeviceStatus.Active, Hz48);

    // --- Comparació de llistes ---

    [Fact]
    public void Llistes_iguals_no_tenen_canvis()
    {
        Assert.Empty(DeviceChanges.Compare([Antena()], [Antena()]));
    }

    [Fact]
    public void Detecta_dispositiu_nou()
    {
        var changes = DeviceChanges.Compare([Antena()], [Antena(), Usb()]);

        var change = Assert.Single(changes);
        Assert.Equal(DeviceChangeType.Added, change.Type);
        Assert.Equal("Taula USB", change.Device.Name);
    }

    [Fact]
    public void Detecta_dispositiu_tret()
    {
        var changes = DeviceChanges.Compare([Antena(), Usb()], [Antena()]);

        var change = Assert.Single(changes);
        Assert.Equal(DeviceChangeType.Removed, change.Type);
        Assert.Equal("Taula USB", change.Device.Name);
    }

    [Fact]
    public void Detecta_cable_desendollat()
    {
        var changes = DeviceChanges.Compare([Antena()], [Antena(DeviceStatus.Unplugged)]);

        var change = Assert.Single(changes);
        Assert.Equal(DeviceChangeType.StatusChanged, change.Type);
        Assert.Equal(DeviceStatus.Active, change.PreviousStatus);
        Assert.Equal(DeviceStatus.Unplugged, change.Device.Status);
    }

    [Fact]
    public void Detecta_nom_i_format_canviats_alhora()
    {
        var changes = DeviceChanges.Compare(
            [Antena()],
            [Antena(name: "Antena principal", format: new DeviceFormat(44100, 16, 2))]);

        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.Type == DeviceChangeType.Renamed && c.PreviousName == "Antena 1");
        Assert.Contains(changes, c => c.Type == DeviceChangeType.FormatChanged && c.PreviousFormat == Hz48);
    }

    // --- Vigilant (amb una llista falsa: no depèn de Windows) ---

    [Fact]
    public async Task Molts_avisos_seguits_donen_un_sol_esdeveniment()
    {
        IReadOnlyList<AudioDevice> current = [Antena()];
        int reads = 0;
        using var watcher = new DeviceWatcher(() => { reads++; return current; }, TimeSpan.FromMilliseconds(100));

        var events = new List<IReadOnlyList<DeviceChange>>();
        var received = new TaskCompletionSource();
        watcher.Changed += changes => { lock (events) events.Add(changes); received.TrySetResult(); };

        watcher.ReadInitialList();
        current = [Antena(DeviceStatus.Unplugged)];

        // Com Windows: diversos avisos per a una sola acció.
        for (int i = 0; i < 10; i++)
        {
            watcher.Poke();
            await Task.Delay(10);
        }

        await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(300); // per si n'arribés un altre

        var only = Assert.Single(events);
        Assert.Equal(DeviceChangeType.StatusChanged, Assert.Single(only).Type);
        Assert.Equal(2, reads); // la lectura inicial i una sola després dels avisos
    }

    [Fact]
    public async Task Sense_canvis_reals_no_hi_ha_esdeveniment()
    {
        using var watcher = new DeviceWatcher(() => [Antena()], TimeSpan.FromMilliseconds(50));
        bool fired = false;
        watcher.Changed += _ => fired = true;

        watcher.ReadInitialList();
        watcher.Poke(); // p. ex. algú ha mogut el volum

        await Task.Delay(300);
        Assert.False(fired);
    }

    [Fact]
    public async Task Un_error_en_llegir_no_atura_el_vigilant()
    {
        bool fail = false;
        IReadOnlyList<AudioDevice> current = [Antena()];
        using var watcher = new DeviceWatcher(
            () => fail ? throw new InvalidOperationException("controlador defectuós") : current,
            TimeSpan.FromMilliseconds(50));

        var error = new TaskCompletionSource<Exception>();
        var changed = new TaskCompletionSource();
        watcher.Error += ex => error.TrySetResult(ex);
        watcher.Changed += _ => changed.TrySetResult();

        watcher.ReadInitialList();

        fail = true;
        watcher.Poke();
        var ex = await error.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("controlador defectuós", ex.Message);

        // Després de l'error, continua funcionant.
        fail = false;
        current = [Antena(), Usb()];
        watcher.Poke();
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    // --- Format de l'esdeveniment ---

    [Fact]
    public void Esdeveniment_devices_changed_porta_els_canvis()
    {
        var ev = DeviceJson.ChangedEvent(
        [
            new DeviceChange(DeviceChangeType.StatusChanged, Antena(DeviceStatus.Unplugged), PreviousStatus: DeviceStatus.Active),
            new DeviceChange(DeviceChangeType.Added, Usb()),
        ]);

        Assert.Equal(1, ev["v"]!.GetValue<int>());
        Assert.Equal("devices.changed", ev["ev"]!.GetValue<string>());

        var changes = ev["changes"]!.AsArray();
        Assert.Equal("status", changes[0]!["change"]!.GetValue<string>());
        Assert.Equal("unplugged", changes[0]!["device"]!["status"]!.GetValue<string>());
        Assert.Equal("active", changes[0]!["previousStatus"]!.GetValue<string>());
        Assert.Equal("added", changes[1]!["change"]!.GetValue<string>());
        Assert.Equal("Taula USB", changes[1]!["device"]!["name"]!.GetValue<string>());
    }
}
