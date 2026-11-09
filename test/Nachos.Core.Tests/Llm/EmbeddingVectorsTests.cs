using Nachos.Abstractions.Domain;
using Nachos.Core.Llm;
using Shouldly;

namespace Nachos.Core.Tests.Llm;

public sealed class EmbeddingVectorsTests
{
    private static float[] RandomVector(Random random, int length) =>
        [.. Enumerable.Range(0, length).Select(_ => (float)(random.NextDouble() * 2 - 1))];

    private static double Dot(float[] a, float[] b) => a.Zip(b, (x, y) => (double)x * y).Sum();

    private static double Cosine(float[] a, float[] b) => Dot(a, b) / Math.Sqrt(Dot(a, a) * Dot(b, b));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ZeroPadding_PreservesCosineDistance(int seed)
    {
        var random = new Random(seed);
        var a = RandomVector(random, 384);
        var b = RandomVector(random, 384);

        var padded = Cosine(EmbeddingVectors.PadTo(a, VectorSpace.Dimensions), EmbeddingVectors.PadTo(b, VectorSpace.Dimensions));

        padded.ShouldBe(Cosine(a, b), tolerance: 1e-12);
        EmbeddingVectors.PadTo(a, VectorSpace.Dimensions).Skip(384).ShouldAllBe(value => value == 0);
    }

    [Fact]
    public void PadTo_ShorterThanTheVector_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => EmbeddingVectors.PadTo(new float[4], 3));
    }

    [Fact]
    public void ClsPooling_TakesFirstToken()
    {
        float[] hidden = [1, 2, 3, 10, 20, 30, 100, 200, 300];

        EmbeddingVectors.Pool(hidden, [1, 1, 0], dimensions: 3, EmbeddingPooling.Cls).ShouldBe([1f, 2f, 3f]);
    }

    [Fact]
    public void MeanPooling_IgnoresMaskedTokens()
    {
        float[] hidden = [1, 2, 3, 3, 4, 5, 100, 200, 300];

        EmbeddingVectors.Pool(hidden, [1, 1, 0], dimensions: 3, EmbeddingPooling.Mean).ShouldBe([2f, 3f, 4f]);
    }

    [Fact]
    public void MeanPooling_NothingKept_Throws()
    {
        Should.Throw<ArgumentException>(() => EmbeddingVectors.Pool(new float[6], [0, 0], dimensions: 3, EmbeddingPooling.Mean));
    }

    [Fact]
    public void Pool_ShapeMismatch_Throws()
    {
        Should.Throw<ArgumentException>(() => EmbeddingVectors.Pool(new float[5], [1, 1], dimensions: 3, EmbeddingPooling.Cls));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Normalize_UnitLength(int seed)
    {
        var vector = RandomVector(new Random(seed), 384).Select(value => value * 7).ToArray();
        var direction = vector.ToArray();

        EmbeddingVectors.Normalize(vector);

        Math.Sqrt(Dot(vector, vector)).ShouldBe(1, tolerance: 1e-5);
        Cosine(vector, direction).ShouldBe(1, tolerance: 1e-5);
    }

    [Fact]
    public void Normalize_ZeroVector_Throws()
    {
        Should.Throw<ArgumentException>(() => EmbeddingVectors.Normalize(new float[3]));
    }
}
