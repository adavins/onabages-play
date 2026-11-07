using System.Diagnostics;
using NAudio.Wave;
using OnaBages.Audio;

namespace OnaBages.Tests;

/// <summary>
/// Botonera d'instantanis (fase 5, pas 28a; D-127): sona a l'instant (l'inici és a la memòria), la resta arriba del disc
/// sense cap salt, diversos botons alhora, aturar amb un fos curt, fitxers que no hi són i la sortida que falla.
/// </summary>
public class CartWallTests : IDisposable
{
    private const int Rate = 48000;
    private const string DeviceId = "{0.0.0.00000000}.{botonera}";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "botonera-" + Guid.NewGuid().ToString("N"));
    private readonly List<FakeOutput> _outputs = [];

    public CartWallTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private CartWall NewCart(bool withDevice = true)
    {
        var cart = new CartWall(_ => { var o = new FakeOutput(Rate); _outputs.Add(o); return o; });
        if (withDevice) cart.SetDevice(DeviceId);
        return cart;
    }

    private FakeOutput Output => _outputs[^1];

    /// <summary>WAV estèreo amb un to de <paramref name="hz"/> Hz i amplitud <paramref name="amplitude"/> (els dos canals iguals).</summary>
    private string Tone(double seconds, double hz = 440, float amplitude = 0.3f, string? name = null)
    {
        string path = Path.Combine(_folder, (name ?? $"to-{Guid.NewGuid():N}") + ".wav");
        int frames = (int)(Rate * seconds);
        var samples = new float[frames * 2];
        for (int i = 0; i < frames; i++)
            samples[i * 2] = samples[i * 2 + 1] = amplitude * (float)Math.Sin(2 * Math.PI * hz * i / Rate);
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2));
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private static void WaitReady(CartWall cart, int page, int index)
    {
        var watch = Stopwatch.StartNew();
        while (cart.ButtonAt(page, index)?.Status == CartButtonStatus.Loading && watch.Elapsed < TimeSpan.FromSeconds(10))
            Thread.Sleep(10);
    }

    /// <summary>Demana <paramref name="seconds"/> a la sortida, en blocs de 10 ms (com Windows), i ho retorna tot.</summary>
    private float[] Pump(double seconds)
    {
        var all = new List<float>();
        int blocks = (int)Math.Round(seconds * 100);
        for (int i = 0; i < blocks; i++)
        {
            all.AddRange(Output.Pump(Rate / 100));
            if (i % 20 == 0) Thread.Sleep(1); // deixa treballar el fil de lectura
        }
        return [.. all];
    }

    private static float Peak(float[] samples, int from = 0, int count = -1)
    {
        if (count < 0) count = samples.Length - from;
        float peak = 0;
        for (int i = from; i < from + count; i++) peak = Math.Max(peak, Math.Abs(samples[i]));
        return peak;
    }

    [Fact]
    public void Assignar_llegeix_l_inici_i_la_durada()
    {
        using var cart = NewCart();
        string path = Tone(5, name: "Sintonia");
        var button = cart.Assign(0, 3, path);

        Assert.Equal("Sintonia", button.Name);   // el nom del fitxer, sense l'extensió
        WaitReady(cart, 0, 3);
        Assert.Equal(CartButtonStatus.Ready, button.Status);
        Assert.Equal(5.0, button.Duration.TotalSeconds, 2);
        Assert.Single(cart.Buttons);
    }

    [Fact]
    public void Sona_a_l_instant_i_tot_el_fitxer_surt_igual_sense_cap_salt()
    {
        using var cart = NewCart();
        string path = Tone(5, hz: 1000);           // més llarg que l'inici a la memòria (3 s)
        cart.Assign(0, 0, path);
        WaitReady(cart, 0, 0);

        Assert.True(cart.Press(0, 0));
        var output = Pump(5.2);

        // El primer bloc ja té so: no s'espera el disc.
        Assert.True(Peak(output, 0, Rate / 100 * 2) > 0.1f);
        // Mostra a mostra igual que el fitxer (també on s'acaba l'inici i comença el que llegeix el fil de lectura).
        using var source = AudioFileSource.Open(path, Rate);
        var expected = TestAudio.ReadAll(source);
        for (int i = 0; i < expected.Length; i++)
            Assert.True(Math.Abs(expected[i] - output[i]) < 1e-6, $"Diferent a la mostra {i} ({i / 2.0 / Rate:F3} s)");
        Assert.Equal(0, cart.Underruns);
        // Quan s'acaba, silenci, i la revisió allibera la veu.
        Assert.Equal(0, Peak(output, expected.Length));
        cart.Tick();
        Assert.Empty(cart.Playing);
    }

    [Fact]
    public void Un_tall_curt_es_tot_a_la_memoria()
    {
        using var cart = NewCart();
        cart.Assign(0, 0, Tone(0.5));
        WaitReady(cart, 0, 0);
        cart.Press(0, 0);
        var output = Pump(0.6);
        Assert.True(Peak(output, 0, Rate * 2 / 2 - 100) > 0.25f);
        Assert.Equal(0, Peak(output, Rate)); // després de 0,5 s, silenci
        cart.Tick();
        Assert.False(cart.IsPlaying(0, 0));
    }

    [Fact]
    public void Diversos_alhora_es_barregen()
    {
        using var cart = NewCart();
        cart.Assign(0, 0, Tone(1, hz: 440, amplitude: 0.2f));
        cart.Assign(0, 1, Tone(1, hz: 440, amplitude: 0.3f));   // mateixa fase: se sumen
        WaitReady(cart, 0, 0);
        WaitReady(cart, 0, 1);

        cart.Press(0, 0);
        cart.Press(0, 1);
        Assert.Equal(2, cart.Playing.Count);
        var output = Pump(0.5);
        Assert.Equal(0.5f, Peak(output), 0.01f);
    }

    [Fact]
    public void El_que_passaria_de_0_dB_es_retalla()
    {
        using var cart = NewCart();
        for (int i = 0; i < 3; i++) cart.Assign(0, i, Tone(1, amplitude: 0.5f));
        for (int i = 0; i < 3; i++) WaitReady(cart, 0, i);
        for (int i = 0; i < 3; i++) cart.Press(0, i);
        Assert.Equal(1f, Peak(Pump(0.3)));
    }

    [Fact]
    public void Premer_mentre_sona_l_atura_amb_un_fos_curt()
    {
        using var cart = NewCart();
        cart.Assign(0, 0, Tone(5, amplitude: 0.3f));
        WaitReady(cart, 0, 0);
        cart.Press(0, 0);
        Pump(0.2);

        Assert.False(cart.Press(0, 0));        // ara s'atura
        var output = Pump(0.1);
        // Baixa en 15 ms, sense tall brusc: el primer ms encara té so; després de 20 ms, silenci.
        Assert.True(Peak(output, 0, 96) > 0.1f);
        Assert.Equal(0, Peak(output, Rate / 50 * 2));
        cart.Tick();
        Assert.False(cart.IsPlaying(0, 0));

        Assert.True(cart.Press(0, 0));         // i torna a començar des del principi
        Assert.True(Peak(Pump(0.05)) > 0.25f);
    }

    [Fact]
    public void Aturar_tot()
    {
        using var cart = NewCart();
        cart.Assign(0, 0, Tone(3));
        cart.Assign(1, 0, Tone(3));
        WaitReady(cart, 0, 0);
        WaitReady(cart, 1, 0);
        cart.Press(0, 0);
        cart.Press(1, 0);
        Pump(0.1);
        cart.StopAll();
        Pump(0.05);
        cart.Tick();
        Assert.Empty(cart.Playing);
    }

    [Fact]
    public void El_volum_del_boto()
    {
        using var cart = NewCart();
        cart.Assign(0, 0, Tone(2, amplitude: 0.5f));
        WaitReady(cart, 0, 0);
        cart.Configure(0, 0, gainDb: -6);
        cart.Press(0, 0);
        Assert.Equal(0.25f, Peak(Pump(0.2)), 0.01f);
        cart.Configure(0, 0, name: "Falca", color: "#d63384", gainDb: 99); // fora de marge: al límit
        var b = cart.ButtonAt(0, 0)!;
        Assert.Equal(("Falca", "#d63384", CartWall.MaxGainDb), (b.Name, b.Color, b.GainDb));
    }

    [Fact]
    public void Sense_sortida_o_sense_fitxer_no_sona()
    {
        using var cart = NewCart(withDevice: false);
        cart.Assign(0, 0, Tone(1));
        WaitReady(cart, 0, 0);
        Assert.Contains("sortida", Assert.Throws<CartCommandException>(() => cart.Press(0, 0)).Message);
        Assert.Throws<CartCommandException>(() => cart.Press(0, 5)); // botó buit

        cart.SetDevice(DeviceId);
        cart.Assign(0, 1, Path.Combine(_folder, "no-hi-es.mp3"));
        WaitReady(cart, 0, 1);
        Assert.Equal(CartButtonStatus.Missing, cart.ButtonAt(0, 1)!.Status);
        Assert.Throws<CartCommandException>(() => cart.Press(0, 1));
        Assert.True(cart.Press(0, 0)); // els altres botons funcionen igual
    }

    [Fact]
    public void Com_a_molt_8_alhora()
    {
        using var cart = NewCart();
        string path = Tone(3);
        for (int i = 0; i <= CartWall.MaxVoices; i++) cart.Assign(0, i, path);
        for (int i = 0; i <= CartWall.MaxVoices; i++) WaitReady(cart, 0, i);
        for (int i = 0; i < CartWall.MaxVoices; i++) cart.Press(0, i);
        Assert.Throws<CartCommandException>(() => cart.Press(0, CartWall.MaxVoices));
    }

    [Fact]
    public void Treure_o_canviar_el_fitxer_atura_el_que_sonava()
    {
        using var cart = NewCart();
        cart.Assign(0, 0, Tone(3));
        WaitReady(cart, 0, 0);
        cart.Press(0, 0);
        Pump(0.1);
        cart.Clear(0, 0);
        Pump(0.05);
        cart.Tick();
        Assert.Empty(cart.Playing);
        Assert.Null(cart.ButtonAt(0, 0));
    }

    [Fact]
    public void Si_la_sortida_falla_es_diu_i_els_talls_s_aturen()
    {
        using var cart = NewCart();
        cart.Assign(0, 0, Tone(3));
        WaitReady(cart, 0, 0);
        cart.Press(0, 0);
        Pump(0.1);

        Output.Fail("desconnectat");
        cart.Tick();

        Assert.False(cart.OutputReady);
        Assert.Contains("desconnectat", cart.ErrorMessage);
        Assert.Empty(cart.Playing);
        Assert.True(Output.Disposed);
    }

    [Fact]
    public void El_fil_d_audio_no_crea_objectes()
    {
        using var cart = NewCart();
        cart.Assign(0, 0, Tone(4));
        cart.Assign(0, 1, Tone(0.5));
        WaitReady(cart, 0, 0);
        WaitReady(cart, 0, 1);
        var buffer = new byte[Rate / 100 * 2 * sizeof(float)];
        var source = Output.Source;

        // Es mesura només el que fa el fil d'àudio (Read). Les ordres (Press, Stop) són del fil de les ordres.
        long Measure(int reads)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < reads; i++) source.Read(buffer);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        // Una volta sencera per escalfar (perquè el compilador JIT ja hagi fet la seva feina).
        for (int round = 0; round < 2; round++)
        {
            cart.Press(0, 0);
            cart.Press(0, 1);
            long playing = Measure(100);   // 1 s: tots dos sonen, el curt s'acaba
            cart.Stop(0, 0);
            long stopping = Measure(10);   // el fos d'aturar
            cart.Tick();
            if (round == 1)
            {
                Assert.Equal(0, playing);
                Assert.Equal(0, stopping);
            }
        }
    }
}
