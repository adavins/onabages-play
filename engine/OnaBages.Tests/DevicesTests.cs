using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages.Engine;
using OnaBages.Engine.Protocol;

namespace OnaBages.Tests;

public class DevicesTests
{
    // Llista inventada: les proves no depenen del maquinari de l'ordinador.
    private static readonly AudioDevice[] FakeDevices =
    [
        new("{0.0.0.00000000}.{a}", "Antena 1 (SB Z)", DeviceKind.Output, DeviceStatus.Active, new DeviceFormat(48000, 24, 2)),
        new("{0.0.0.00000000}.{b}", "Preescolta (Realtek)", DeviceKind.Output, DeviceStatus.Unplugged, null),
        new("{0.0.1.00000000}.{c}", "Línia (Realtek)", DeviceKind.Input, DeviceStatus.Active, new DeviceFormat(44100, 16, 2)),
        new("{0.0.1.00000000}.{d}", "Mescla estèreo", DeviceKind.Input, DeviceStatus.Disabled, null),
    ];

    private static JsonObject Send(string json) =>
        new CommandRouter(new EngineState(), () => { }, () => FakeDevices).Handle(json);

    private static string[] Names(JsonObject response) =>
        response["result"]!["devices"]!.AsArray().Select(d => d!["name"]!.GetValue<string>()).ToArray();

    [Fact]
    public void Per_defecte_retorna_nomes_els_actius()
    {
        var r = Send("""{ "v": 1, "id": 1, "cmd": "devices.list" }""");

        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.Equal(["Antena 1 (SB Z)", "Línia (Realtek)"], Names(r));
    }

    [Fact]
    public void Amb_includeInactive_retorna_tots()
    {
        var r = Send("""{ "v": 1, "id": 1, "cmd": "devices.list", "includeInactive": true }""");

        Assert.Equal(4, Names(r).Length);
    }

    [Theory]
    [InlineData("output", "Antena 1 (SB Z)")]
    [InlineData("input", "Línia (Realtek)")]
    public void Filtra_per_tipus(string kind, string expected)
    {
        var r = Send($$"""{ "v": 1, "id": 1, "cmd": "devices.list", "kind": "{{kind}}" }""");

        Assert.Equal([expected], Names(r));
    }

    [Fact]
    public void Tipus_incorrecte_retorna_bad_params()
    {
        var r = Send("""{ "v": 1, "id": 9, "cmd": "devices.list", "kind": "altaveus" }""");

        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Equal(9, r["id"]!.GetValue<int>());
        Assert.Equal("bad_params", r["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public void Cada_dispositiu_porta_id_nom_tipus_estat_i_format()
    {
        var r = Send("""{ "v": 1, "id": 1, "cmd": "devices.list", "includeInactive": true }""");
        var devices = r["result"]!["devices"]!.AsArray();

        var antena = devices[0]!;
        Assert.Equal("{0.0.0.00000000}.{a}", antena["id"]!.GetValue<string>());
        Assert.Equal("output", antena["kind"]!.GetValue<string>());
        Assert.Equal("active", antena["status"]!.GetValue<string>());
        Assert.Equal(48000, antena["format"]!["sampleRate"]!.GetValue<int>());
        Assert.Equal(24, antena["format"]!["bits"]!.GetValue<int>());
        Assert.Equal(2, antena["format"]!["channels"]!.GetValue<int>());

        var preescolta = devices[1]!;
        Assert.Equal("unplugged", preescolta["status"]!.GetValue<string>());
        Assert.Null(preescolta["format"]);

        Assert.Equal("disabled", devices[3]!["status"]!.GetValue<string>());
    }

    [Fact]
    public void Interpreta_format_ampliat_de_24_bits()
    {
        // WAVEFORMATEXTENSIBLE: 2 canals, 48000 Hz, contenidor de 32 bits amb 24 bits vàlids.
        byte[] data =
        [
            0xFE, 0xFF, 0x02, 0x00, 0x80, 0xBB, 0x00, 0x00, 0x00, 0x77, 0x05, 0x00,
            0x08, 0x00, 0x20, 0x00, 0x16, 0x00, 0x18, 0x00, 0x03, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA,
            0x00, 0x38, 0x9B, 0x71,
        ];

        Assert.Equal(new DeviceFormat(48000, 24, 2), DeviceList.ParseWaveFormat(data));
    }

    [Fact]
    public void Interpreta_format_simple_de_16_bits()
    {
        // WAVEFORMATEX PCM: 1 canal, 44100 Hz, 16 bits.
        byte[] data = [0x01, 0x00, 0x01, 0x00, 0x44, 0xAC, 0x00, 0x00, 0x88, 0x58, 0x01, 0x00, 0x02, 0x00, 0x10, 0x00];

        Assert.Equal(new DeviceFormat(44100, 16, 1), DeviceList.ParseWaveFormat(data));
    }

    [Fact]
    public void Format_massa_curt_retorna_null()
    {
        Assert.Null(DeviceList.ParseWaveFormat([0x01, 0x00]));
    }

    [Fact]
    public void Llegir_els_dispositius_reals_no_falla()
    {
        // Depèn de l'ordinador: només comprova que la lectura de Windows funciona
        // i que les dades tenen sentit. Un ordinador sense àudio retornaria una llista buida.
        var devices = DeviceList.GetAll();

        Assert.All(devices, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Id));
            Assert.False(string.IsNullOrWhiteSpace(d.Name));
        });
    }
}
