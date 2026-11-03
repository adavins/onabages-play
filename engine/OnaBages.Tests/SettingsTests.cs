using System.Text.Json.Nodes;
using OnaBages.Engine;
using OnaBages.Engine.Data;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

/// <summary>Ordres settings.get i settings.set (zoom de la interfície), desades a config.json.</summary>
public class SettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "settings-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigRepository _repository;
    private readonly CommandRouter _router;

    public SettingsTests()
    {
        _repository = new ConfigRepository(Path.Combine(_folder, "config.json"));
        var store = new ConfigStore(_repository);
        store.Load();
        _router = new CommandRouter(new EngineState(), () => { }, () => [], config: store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static double Zoom(JsonObject r) => r["result"]!["settings"]!["ui"]!["zoom"]!.GetValue<double>();

    private static string ErrorCode(JsonObject r) => r["error"]!["code"]!.GetValue<string>();

    [Fact]
    public void Per_defecte_el_zoom_es_100()
    {
        var r = _router.Handle("""{ "v": 1, "id": 1, "cmd": "settings.get" }""");

        Assert.Equal(1.0, Zoom(r));
    }

    [Fact]
    public void El_zoom_es_desa_i_es_conserva()
    {
        var r = _router.Handle("""{ "v": 1, "id": 1, "cmd": "settings.set", "ui": { "zoom": 1.25 } }""");

        Assert.Equal(1.25, Zoom(r));
        Assert.Equal(1.25, _repository.Load().Config.UiZoom);
    }

    [Theory]
    [InlineData("0.4")]
    [InlineData("2")]
    [InlineData("\"gran\"")]
    public void Zoom_fora_de_marge_dona_bad_params(string value)
    {
        var r = _router.Handle($$"""{ "v": 1, "id": 1, "cmd": "settings.set", "ui": { "zoom": {{value}} } }""");

        Assert.Equal("bad_params", ErrorCode(r));
        Assert.Equal(1.0, _repository.Load().Config.UiZoom);
    }

    [Fact]
    public void Un_fitxer_antic_sense_ui_es_llegeix_igualment()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(_repository.FilePath, """{ "schemaVersion": 1, "players": [], "recorders": [], "knownDevices": [] }""");

        var loaded = _repository.Load();

        Assert.Null(loaded.Problem);
        Assert.Equal(1.0, loaded.Config.UiZoom);
    }
}
