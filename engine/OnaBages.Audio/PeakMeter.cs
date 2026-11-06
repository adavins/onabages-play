namespace OnaBages.Audio;

/// <summary>
/// Nivell de pic dels dos canals, per als mesuradors.
///
/// El fil d'àudio hi apunta el pic de cada bloc (<see cref="Add"/>) i el temporitzador
/// dels mesuradors el recull i el posa a zero (<see cref="Take"/>), unes 25 vegades per segon.
/// Sense bloquejos ni objectes nous: es pot cridar des del fil d'àudio.
/// </summary>
public sealed class PeakMeter
{
    // Els valors són positius: els seus bits, llegits com a enter, mantenen l'ordre.
    // Així es pot guardar el màxim amb operacions atòmiques d'enters.
    private int _left;
    private int _right;

    /// <summary>Apunta el pic d'un bloc (valors absoluts, 0..1 o més).</summary>
    public void Add(float left, float right)
    {
        Max(ref _left, left);
        Max(ref _right, right);
    }

    /// <summary>
    /// Pic de mostres estèreo (o d'un sol canal duplicat) en un bloc intercalat.
    /// </summary>
    /// <param name="samples">Mostres intercalades.</param>
    /// <param name="channels">Canals del bloc. Es mesuren els dos primers (un sol canal compta per als dos).</param>
    /// <param name="gain">Guany lineal que s'hi aplicarà després (p. ex. el volum d'enregistrament).</param>
    /// <param name="ceiling">
    /// Sostre lineal d'un limitador que s'hi aplicarà després (p. ex. el dels enregistradors, D-083): el pic no el
    /// passa, perquè el limitador ho garanteix (i per sota del sostre no toca res). Per defecte, sense sostre.
    /// </param>
    public void AddInterleaved(ReadOnlySpan<float> samples, int channels, float gain = 1f, float ceiling = float.MaxValue)
    {
        float left = 0, right = 0;
        int second = channels > 1 ? 1 : 0;
        for (int i = 0; i + channels <= samples.Length; i += channels)
        {
            float l = Math.Abs(samples[i]);
            float r = Math.Abs(samples[i + second]);
            if (l > left) left = l;
            if (r > right) right = r;
        }
        Add(Math.Min(left * gain, ceiling), Math.Min(right * gain, ceiling));
    }

    /// <summary>Retorna el pic des de l'última crida i el torna a posar a zero.</summary>
    public (float Left, float Right) Take() =>
        (BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _left, 0)),
         BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _right, 0)));

    /// <summary>Converteix un pic (0..1) a dBFS, amb un mínim de <paramref name="floorDb"/>.</summary>
    public static double ToDb(float peak, double floorDb = -90) =>
        peak <= 0 ? floorDb : Math.Max(floorDb, Math.Round(20 * Math.Log10(peak), 1));

    private static void Max(ref int target, float value)
    {
        if (!(value > 0)) return; // també descarta NaN
        int bits = BitConverter.SingleToInt32Bits(value);
        int current = Volatile.Read(ref target);
        while (bits > current)
        {
            int seen = Interlocked.CompareExchange(ref target, bits, current);
            if (seen == current) return;
            current = seen;
        }
    }
}
