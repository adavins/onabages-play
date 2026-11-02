using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>Configuració desada i comparació del maquinari en arrencar (especificació 5.6), amb dispositius inventats.</summary>
public class HardwareTests : IDisposable
{
    private static readonly DeviceFormat Cd = new(44100, 16, 2);
    private static readonly DeviceFormat Dvd = new(48000, 24, 2);

    private static readonly AudioDevice SoundBlaster = Out("{sb}", "Altavoces (Sound Blaster Z)");
    private static readonly AudioDevice Realtek = Out("{rt}", "Altavoces (Realtek)");
    private static readonly AudioDevice LineIn = In("{li}", "Línea de entrada (Realtek)");
    private static readonly AudioDevice UsbIn = In("{usb-1}", "Micrófono (USB Audio CODEC)");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "hw-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigRepository _repository;

    public HardwareTests()
    {
        _repository = new ConfigRepository(Path.Combine(_folder, "config.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static AudioDevice Out(string id, string name, DeviceStatus status = DeviceStatus.Active, DeviceFormat? format = null) =>
        new(id, name, DeviceKind.Output, status, format ?? Dvd);

    private static AudioDevice In(string id, string name, DeviceStatus status = DeviceStatus.Active, DeviceFormat? format = null) =>
        new(id, name, DeviceKind.Input, status, format ?? Dvd);

    private static DeviceRef Ref(AudioDevice d) => new(d.Id, d.Name);

    private static EngineState NewState() => new(
        [new Player("1", _ => new FakeOutput()), new Player("2", _ => new FakeOutput())],
        [new Recorder("1", _ => new FakeInput()), new Recorder("2", _ => new FakeInput())]);

    /// <summary>Desa una configuració com si fos d'una sessió anterior.</summary>
    private void SaveConfig(Action<EngineConfig> fill, params AudioDevice[] known)
    {
        var config = new EngineConfig { KnownDevices = known.ToList() };
        fill(config);
        _repository.Save(config);
    }

    // -----------------------------------------------------------------------
    // Repositori
    // -----------------------------------------------------------------------

    [Fact]
    public void Repositori_desa_i_llegeix_el_mateix()
    {
        SaveConfig(c =>
        {
            c.Players["1"] = Ref(SoundBlaster);
            c.Recorders["2"] = Ref(LineIn);
        }, SoundBlaster, LineIn, Out("{x}", "Sense format", DeviceStatus.Unplugged) with { Format = null });

        var loaded = _repository.Load();

        Assert.Null(loaded.Problem);
        Assert.Equal(Ref(SoundBlaster), loaded.Config.Players["1"]);
        Assert.Equal(Ref(LineIn), loaded.Config.Recorders["2"]);
        Assert.Equal(3, loaded.Config.KnownDevices.Count);
        Assert.Equal(SoundBlaster, loaded.Config.KnownDevices[0]);
        Assert.Equal(LineIn, loaded.Config.KnownDevices[1]);
        Assert.Equal(DeviceStatus.Unplugged, loaded.Config.KnownDevices[2].Status);
        Assert.Null(loaded.Config.KnownDevices[2].Format);

        var json = JsonNode.Parse(File.ReadAllText(_repository.FilePath))!;
        Assert.Equal(ConfigRepository.SchemaVersion, json["schemaVersion"]!.GetValue<int>());
        Assert.False(File.Exists(_repository.FilePath + ".tmp"));
    }

    [Fact]
    public void Sense_fitxer_la_configuracio_es_buida()
    {
        var loaded = _repository.Load();

        Assert.Null(loaded.Problem);
        Assert.Empty(loaded.Config.Players);
        Assert.Empty(loaded.Config.KnownDevices);
    }

    [Fact]
    public void Fitxer_malmes_dona_avis_i_en_guarda_una_copia()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(_repository.FilePath, """{ "schemaVersion": 1, "players": [ """);

        var loaded = _repository.Load();

        Assert.NotNull(loaded.Problem);
        Assert.Empty(loaded.Config.Players);
        Assert.Equal("""{ "schemaVersion": 1, "players": [ """, File.ReadAllText(_repository.DamagedCopyPath));
    }

    [Fact]
    public void Versio_desconeguda_no_es_llegeix()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(_repository.FilePath, """{ "schemaVersion": 99, "players": [] }""");

        var loaded = _repository.Load();

        Assert.Contains("99", loaded.Problem);
        Assert.True(File.Exists(_repository.DamagedCopyPath));
    }

    // -----------------------------------------------------------------------
    // Comparació (taula de 5.6)
    // -----------------------------------------------------------------------

    [Fact]
    public void Mateix_identificador_actiu_es_correcte()
    {
        var (device, result) = HardwareCheck.Find(Ref(SoundBlaster), DeviceKind.Output, [Realtek, SoundBlaster]);

        Assert.Equal(CheckResult.Ok, result);
        Assert.Equal(SoundBlaster, device);
    }

    [Fact]
    public void Desconnectat_o_desactivat_es_troba_igualment()
    {
        var unplugged = Realtek with { Status = DeviceStatus.Unplugged };
        var disabled = SoundBlaster with { Status = DeviceStatus.Disabled };

        Assert.Equal(CheckResult.Unplugged, HardwareCheck.Find(Ref(Realtek), DeviceKind.Output, [unplugged]).Result);
        Assert.Equal(CheckResult.Disabled, HardwareCheck.Find(Ref(SoundBlaster), DeviceKind.Output, [disabled]).Result);
    }

    [Fact]
    public void Identificador_nou_amb_un_sol_nom_igual_es_el_mateix()
    {
        var moved = UsbIn with { Id = "{usb-2}" }; // el mateix USB en un altre port

        var (device, result) = HardwareCheck.Find(Ref(UsbIn), DeviceKind.Input, [LineIn, moved]);

        Assert.Equal(CheckResult.IdChanged, result);
        Assert.Equal("{usb-2}", device!.Id);
    }

    [Fact]
    public void Diversos_amb_el_mateix_nom_no_se_n_tria_cap()
    {
        var a = UsbIn with { Id = "{usb-2}" };
        var b = UsbIn with { Id = "{usb-3}" };

        var (device, result) = HardwareCheck.Find(Ref(UsbIn), DeviceKind.Input, [a, b]);

        Assert.Equal(CheckResult.Ambiguous, result);
        Assert.Null(device);
    }

    [Fact]
    public void Si_no_hi_es_no_es_tria_cap_altre()
    {
        var (device, result) = HardwareCheck.Find(Ref(SoundBlaster), DeviceKind.Output, [Realtek]);

        Assert.Equal(CheckResult.Missing, result);
        Assert.Null(device);
    }

    [Fact]
    public void Una_entrada_no_es_confon_amb_una_sortida_del_mateix_nom()
    {
        var input = In("{other}", SoundBlaster.Name);

        Assert.Equal(CheckResult.Missing, HardwareCheck.Find(Ref(SoundBlaster), DeviceKind.Output, [input]).Result);
    }

    [Fact]
    public void Avisa_dels_dispositius_nous_i_dels_formats_canviats()
    {
        var config = new EngineConfig { KnownDevices = [SoundBlaster, Realtek with { Format = Cd }] };
        config.Players["1"] = Ref(SoundBlaster);
        var newUsb = UsbIn;

        var report = HardwareCheck.Compare(config, [(ElementKind.Player, "1")], [SoundBlaster, Realtek, newUsb], firstRun: false, configProblem: null);

        Assert.Equal(newUsb, Assert.Single(report.NewDevices));
        var change = Assert.Single(report.FormatChanges);
        Assert.Equal(Cd, change.PreviousFormat);
        Assert.Equal(Dvd, change.Device.Format);
        Assert.True(report.HasChanges);
    }

    [Fact]
    public void Sense_canvis_el_resum_no_te_res_a_dir()
    {
        var config = new EngineConfig { KnownDevices = [SoundBlaster, LineIn] };
        config.Players["1"] = Ref(SoundBlaster);

        var report = HardwareCheck.Compare(config, [(ElementKind.Player, "1")], [SoundBlaster, LineIn], firstRun: false, configProblem: null);

        Assert.False(report.HasChanges);
        Assert.Equal(CheckResult.Ok, Assert.Single(report.Elements).Result);
    }

    // -----------------------------------------------------------------------
    // Assignació en arrencar i en calent
    // -----------------------------------------------------------------------

    [Fact]
    public void Primera_arrencada_no_assigna_res()
    {
        var state = NewState();
        var hardware = new HardwareAssignments(state, _repository);

        var report = hardware.Start([SoundBlaster, Realtek, LineIn]);

        Assert.True(report.FirstRun);
        Assert.Empty(report.NewDevices); // a la primera arrencada no es diu que tot és nou
        Assert.All(state.Players, p => Assert.Null(p.DeviceId));
        Assert.Equal(3, _repository.Load().Config.KnownDevices.Count);
    }

    [Fact]
    public void En_arrencar_assigna_el_que_troba_i_deixa_sense_dispositiu_el_que_falta()
    {
        var moved = UsbIn with { Id = "{usb-2}" };
        SaveConfig(c =>
        {
            c.Players["1"] = Ref(SoundBlaster);
            c.Players["2"] = Ref(Realtek);
            c.Recorders["1"] = Ref(LineIn);
            c.Recorders["2"] = Ref(UsbIn);
        }, SoundBlaster, Realtek, LineIn, UsbIn);
        var state = NewState();
        var hardware = new HardwareAssignments(state, _repository);

        // Sound Blaster treta, Realtek sense cable, USB en un altre port.
        var report = hardware.Start([Realtek with { Status = DeviceStatus.Unplugged }, LineIn, moved]);

        Assert.False(report.FirstRun);
        Assert.Null(state.FindPlayer("1")!.DeviceId);
        Assert.True(hardware.IsWaiting(ElementKind.Player, "1"));
        Assert.Equal(Realtek.Id, state.FindPlayer("2")!.DeviceId);
        Assert.Equal(LineIn.Id, state.FindRecorder("1")!.DeviceId);
        Assert.Equal("{usb-2}", state.FindRecorder("2")!.DeviceId);

        var results = report.Elements.ToDictionary(e => (e.Kind, e.ElementId), e => e.Result);
        Assert.Equal(CheckResult.Missing, results[(ElementKind.Player, "1")]);
        Assert.Equal(CheckResult.Unplugged, results[(ElementKind.Player, "2")]);
        Assert.Equal(CheckResult.Ok, results[(ElementKind.Recorder, "1")]);
        Assert.Equal(CheckResult.IdChanged, results[(ElementKind.Recorder, "2")]);

        // Es desa l'identificador nou, i es recorda el dispositiu que falta.
        var saved = _repository.Load().Config;
        Assert.Equal("{usb-2}", saved.Recorders["2"].Id);
        Assert.Equal(Ref(SoundBlaster), saved.Players["1"]);
    }

    [Fact]
    public void Si_hi_ha_dos_iguals_queda_sense_dispositiu_i_no_s_espera()
    {
        SaveConfig(c => c.Recorders["1"] = Ref(UsbIn), UsbIn);
        var state = NewState();
        var hardware = new HardwareAssignments(state, _repository);

        hardware.Start([UsbIn with { Id = "{usb-2}" }, UsbIn with { Id = "{usb-3}" }]);

        Assert.Null(state.FindRecorder("1")!.DeviceId);
        Assert.False(hardware.IsWaiting(ElementKind.Recorder, "1"));
    }

    [Fact]
    public void Quan_torna_el_dispositiu_es_torna_a_assignar_sense_sonar()
    {
        SaveConfig(c => c.Players["1"] = Ref(SoundBlaster), SoundBlaster, Realtek);
        var state = NewState();
        var hardware = new HardwareAssignments(state, _repository);
        hardware.Start([Realtek]);

        Assert.Empty(hardware.DevicesChanged([Realtek, UsbIn])); // un altre dispositiu no compta

        var reassigned = hardware.DevicesChanged([Realtek, SoundBlaster]);

        var check = Assert.Single(reassigned);
        Assert.Equal("1", check.ElementId);
        Assert.Equal(SoundBlaster.Id, state.FindPlayer("1")!.DeviceId);
        Assert.Equal(PlayerState.Empty, state.FindPlayer("1")!.State);
        Assert.False(hardware.IsWaiting(ElementKind.Player, "1"));
    }

    [Fact]
    public void Si_l_operador_en_tria_un_altre_ja_no_s_espera_l_antic()
    {
        SaveConfig(c => c.Players["1"] = Ref(SoundBlaster), SoundBlaster, Realtek);
        var state = NewState();
        var hardware = new HardwareAssignments(state, _repository);
        hardware.Start([Realtek]);

        state.FindPlayer("1")!.SetDevice(Realtek.Id);
        hardware.Remember(ElementKind.Player, "1", Realtek.Id, [Realtek]);
        var reassigned = hardware.DevicesChanged([Realtek, SoundBlaster]);

        Assert.Empty(reassigned);
        Assert.Equal(Realtek.Id, state.FindPlayer("1")!.DeviceId);
        Assert.Equal(Ref(Realtek), _repository.Load().Config.Players["1"]);
    }

    [Fact]
    public void Configuracio_malmesa_arrenca_igualment()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(_repository.FilePath, "no és JSON");
        var hardware = new HardwareAssignments(NewState(), _repository);
        string? warning = null;
        hardware.Warning += w => warning = w;

        var report = hardware.Start([SoundBlaster]);

        Assert.NotNull(report.ConfigProblem);
        Assert.NotNull(warning);
        Assert.True(report.HasChanges);
        Assert.Null(_repository.Load().Problem); // s'ha tornat a escriure un fitxer vàlid
        Assert.True(File.Exists(_repository.DamagedCopyPath));
    }

    // -----------------------------------------------------------------------
    // Ordres
    // -----------------------------------------------------------------------

    [Fact]
    public void Les_ordres_setDevice_desen_la_configuracio_i_el_resum_es_pot_demanar()
    {
        SaveConfig(c => c.Players["2"] = Ref(SoundBlaster), SoundBlaster, Realtek, LineIn);
        var state = NewState();
        var hardware = new HardwareAssignments(state, _repository);
        hardware.Start([Realtek, LineIn]);
        var router = new CommandRouter(state, () => { }, () => [Realtek, LineIn], hardware);

        router.Handle("""{ "v": 1, "id": 1, "cmd": "player.setDevice", "player": "1", "deviceId": "{rt}" }""");
        router.Handle("""{ "v": 1, "id": 2, "cmd": "recorder.setDevice", "recorder": "1", "deviceId": "{li}" }""");

        var saved = _repository.Load().Config;
        Assert.Equal(Ref(Realtek), saved.Players["1"]);
        Assert.Equal(Ref(LineIn), saved.Recorders["1"]);
        Assert.Equal(Ref(SoundBlaster), saved.Players["2"]); // el que falta es continua recordant

        var r = router.Handle("""{ "v": 1, "id": 3, "cmd": "hardware.report" }""");
        var report = r["result"]!["report"]!;
        Assert.True(report["hasChanges"]!.GetValue<bool>());
        var element = Assert.Single(report["elements"]!.AsArray())!;
        Assert.Equal("player", element["element"]!.GetValue<string>());
        Assert.Equal("2", element["id"]!.GetValue<string>());
        Assert.Equal("missing", element["result"]!.GetValue<string>());
        Assert.Equal(SoundBlaster.Name, element["saved"]!["name"]!.GetValue<string>());
        Assert.Null(element["device"]);

        var stateGet = router.Handle("""{ "v": 1, "id": 4, "cmd": "state.get" }""");
        Assert.True(stateGet["result"]!["hardware"]!["hasChanges"]!.GetValue<bool>());
    }

    [Fact]
    public void Esdeveniment_de_reassignacio()
    {
        var check = new ElementCheck(ElementKind.Recorder, "2", Ref(UsbIn), UsbIn, CheckResult.Ok);

        var ev = HardwareJson.ReassignedEvent(check);

        Assert.Equal("hardware.reassigned", ev["ev"]!.GetValue<string>());
        Assert.Equal("recorder", ev["element"]!["element"]!.GetValue<string>());
        Assert.Equal(UsbIn.Id, ev["element"]!["device"]!["id"]!.GetValue<string>());
    }
}
