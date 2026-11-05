namespace OnaBages.Audio;

/// <summary>
/// Encadenament amb fos (fase 5; D-077). El fil de lectura d'un reproductor no escriu de seguida els últims
/// mil·lisegons que ha llegit: els guarda aquí (la «cua»). Quan la pista s'acaba i n'ha de venir una altra
/// sola (avanç automàtic), la cua es barreja amb el principi de la següent; si no, s'escriu tal qual.
/// Es fa al fil de lectura, mai al d'àudio.
/// </summary>
public sealed class CrossfadeTail
{
    private float[] _buffer = [];
    private int _start;   // primera mostra guardada (posició circular)
    private int _count;   // mostres guardades
    private int _capacity;

    /// <summary>Mostres (intercalades) que es guarden com a molt: la durada del fos.</summary>
    public int Capacity => _capacity;

    /// <summary>Mostres guardades ara.</summary>
    public int Count => _count;

    /// <summary>Buida la cua i en fixa la mida (0 = sense fos: tot passa de llarg).</summary>
    public void Reset(int capacitySamples)
    {
        if (_buffer.Length < capacitySamples) _buffer = new float[capacitySamples];
        _capacity = capacitySamples;
        _start = 0;
        _count = 0;
    }

    /// <summary>
    /// Afegeix mostres a la cua. Les que hi sobren (les més antigues) es copien a <paramref name="overflow"/>,
    /// en ordre, perquè s'escriguin ja. Retorna quantes n'hi ha copiat. <paramref name="overflow"/> ha de
    /// ser com a mínim tan llarg com <paramref name="input"/>.
    /// </summary>
    public int Push(ReadOnlySpan<float> input, Span<float> overflow)
    {
        int excess = _count + input.Length - _capacity;
        if (excess <= 0)
        {
            Append(input);
            return 0;
        }

        // Primer surten les més antigues de la cua; si l'entrada és més llarga que la cua, també el seu principi.
        int fromHeld = Math.Min(excess, _count);
        CopyOut(fromHeld, overflow);
        int fromInput = excess - fromHeld;
        input[..fromInput].CopyTo(overflow[fromHeld..]);
        Append(input[fromInput..]);
        return excess;
    }

    /// <summary>Treu tota la cua, en ordre, a <paramref name="destination"/>. Retorna quantes mostres.</summary>
    public int Drain(Span<float> destination)
    {
        int count = _count;
        CopyOut(count, destination);
        return count;
    }

    private void Append(ReadOnlySpan<float> input)
    {
        for (int i = 0; i < input.Length; i++)
            _buffer[(_start + _count + i) % _capacity] = input[i];
        _count += input.Length;
    }

    private void CopyOut(int count, Span<float> destination)
    {
        for (int i = 0; i < count; i++)
            destination[i] = _buffer[(_start + i) % _capacity];
        if (_capacity > 0) _start = (_start + count) % _capacity;
        _count -= count;
    }

    /// <summary>
    /// Barreja de les dues pistes: <paramref name="ending"/> (la cua de la que s'acaba) baixa i
    /// <paramref name="starting"/> (el principi de la següent) puja, amb corbes d'igual potència
    /// (el volum no baixa a la meitat del fos). Totes dues tenen <paramref name="samples"/> mostres
    /// estèreo intercalades; el resultat va a <paramref name="ending"/>.
    /// </summary>
    public static void Mix(Span<float> ending, ReadOnlySpan<float> starting, int samples)
    {
        int frames = samples / 2;
        for (int f = 0; f < frames; f++)
        {
            double t = (f + 0.5) / frames;
            float fadeOut = (float)Math.Cos(t * Math.PI / 2);
            float fadeIn = (float)Math.Sin(t * Math.PI / 2);
            int i = f * 2;
            ending[i] = ending[i] * fadeOut + starting[i] * fadeIn;
            ending[i + 1] = ending[i + 1] * fadeOut + starting[i + 1] * fadeIn;
        }
    }
}
