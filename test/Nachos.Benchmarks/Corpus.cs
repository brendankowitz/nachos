using System.Text;

namespace Nachos.Benchmarks;

/// <summary>
/// The synthetic, lorem-style corpus: a vocabulary of made-up words drawn with a Zipf distribution (so a few words are in
/// most messages and most words are rare, as in natural text), and unit-norm random vectors. Deterministic for a seed.
/// </summary>
internal sealed class Corpus
{
    public const int Dimensions = 1536;

    private static readonly string[] Syllables =
        ["ka", "lo", "mi", "ne", "su", "ta", "ri", "po", "ve", "zu", "ba", "de", "fi", "go", "hu", "ja", "ke", "lu", "mo", "ni"];

    private readonly string[] _words;
    private readonly double[] _cumulative;

    public Corpus(int vocabularySize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(vocabularySize, Syllables.Length * Syllables.Length);
        _words = [.. Enumerable.Range(0, vocabularySize).Select(Word)];
        _cumulative = new double[vocabularySize];
        var total = 0.0;
        for (var rank = 0; rank < vocabularySize; rank++)
        {
            total += 1.0 / (rank + 1);
            _cumulative[rank] = total;
        }

        for (var rank = 0; rank < vocabularySize; rank++)
        {
            _cumulative[rank] /= total;
        }
    }

    public int VocabularySize => _words.Length;

    /// <summary>The word of Zipf rank <paramref name="rank"/> (0 is the most frequent).</summary>
    public string WordAt(int rank) => _words[rank];

    /// <summary>A word drawn with the corpus's Zipf distribution.</summary>
    public string NextWord(Random random)
    {
        var index = Array.BinarySearch(_cumulative, random.NextDouble());
        return _words[index >= 0 ? index : Math.Min(~index, _words.Length - 1)];
    }

    public string Text(Random random, int words)
    {
        var text = new StringBuilder(words * 8);
        for (var i = 0; i < words; i++)
        {
            text.Append(i == 0 ? "" : " ").Append(NextWord(random));
        }

        return text.ToString();
    }

    /// <summary>Fills <paramref name="vector"/> with a uniformly random direction (Gaussian components, normalized).</summary>
    public static void FillUnitVector(Random random, float[] vector)
    {
        var norm = 0.0;
        for (var i = 0; i < vector.Length; i++)
        {
            // Box-Muller; 1 - NextDouble() is never 0, so the log is finite.
            var gaussian = Math.Sqrt(-2.0 * Math.Log(1.0 - random.NextDouble())) * Math.Cos(2.0 * Math.PI * random.NextDouble());
            vector[i] = (float)gaussian;
            norm += gaussian * gaussian;
        }

        var scale = (float)(1.0 / Math.Sqrt(norm));
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] *= scale;
        }
    }

    /// <summary>The rank written in base 20 with one syllable per digit, at least two: unique, and never an English stopword.</summary>
    private static string Word(int rank)
    {
        var digits = new List<string>();
        var rest = rank;
        do
        {
            digits.Add(Syllables[rest % Syllables.Length]);
            rest /= Syllables.Length;
        }
        while (rest > 0);

        // Pads to two digits with the zero syllable; the length then still identifies the range, so words stay unique.
        if (digits.Count == 1)
        {
            digits.Add(Syllables[0]);
        }

        digits.Reverse();
        return string.Concat(digits);
    }
}
