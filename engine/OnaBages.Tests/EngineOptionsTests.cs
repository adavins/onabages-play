using OnaBages.Engine;

namespace OnaBages.Tests;

public class EngineOptionsTests
{
    [Fact]
    public void Sense_arguments_fa_servir_el_port_per_defecte_i_genera_testimoni()
    {
        var o = EngineOptions.Parse([]);

        Assert.Equal(EngineOptions.DefaultPort, o.Port);
        Assert.Equal(48, o.Token.Length);
    }

    [Fact]
    public void Llegeix_port_i_testimoni()
    {
        var o = EngineOptions.Parse(["--port", "50000", "--token", "abc"]);

        Assert.Equal(50000, o.Port);
        Assert.Equal("abc", o.Token);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("hola")]
    public void Port_no_valid_llanca_error(string port)
    {
        Assert.Throws<ArgumentException>(() => EngineOptions.Parse(["--port", port]));
    }

    [Fact]
    public void Comparacio_de_testimonis()
    {
        Assert.True(EngineOptions.TokenMatches("abc", "abc"));
        Assert.False(EngineOptions.TokenMatches("abd", "abc"));
        Assert.False(EngineOptions.TokenMatches("", "abc"));
        Assert.False(EngineOptions.TokenMatches(null, "abc"));
    }

    // --- Mode portable (D-044) ---

    [Fact]
    public void Sense_carpeta_de_dades_es_fa_servir_la_de_l_usuari()
    {
        Assert.Null(EngineOptions.Parse([]).DataDir);
    }

    [Fact]
    public void Carpeta_de_dades_amb_ruta_completa()
    {
        var o = EngineOptions.Parse(["--data-dir", @"D:\OnaBagesPlay\dades", "--port", "50000"]);

        Assert.Equal(@"D:\OnaBagesPlay\dades", o.DataDir);
        Assert.Equal(50000, o.Port);
    }

    [Fact]
    public void Carpeta_de_dades_relativa_no_s_accepta()
    {
        Assert.Throws<ArgumentException>(() => EngineOptions.Parse(["--data-dir", "dades"]));
    }
}
