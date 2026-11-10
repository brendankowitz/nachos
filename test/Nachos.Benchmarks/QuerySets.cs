using Nachos.Abstractions.Domain;

namespace Nachos.Benchmarks;

/// <summary>
/// The queries of every series, fixed by a seed so each scale step runs the same ones. A query index wraps around.
/// <list type="bullet">
/// <item>Text queries: three to five words drawn from the corpus distribution, as a user would type them.</item>
/// <item>Term sets for the lexical store: one rare word; and three or eight words of which one is common (in roughly half
/// of the messages) and the rest of middling frequency.</item>
/// <item>Vectors: unit-norm random, like the seeded chunks.</item>
/// </list>
/// </summary>
internal sealed class QuerySets
{
    private readonly string[] _texts;
    private readonly string[] _sessions;
    private readonly string[] _peers;
    private readonly float[][] _vectors;
    private readonly string[] _common;

    public QuerySets(Corpus corpus, SeedWorkspace workspace, int peerCount, int seed, int count)
    {
        var random = new Random(seed);
        _texts = [.. Enumerable.Range(0, count).Select(_ => corpus.Text(random, random.Next(3, 6)))];
        _sessions = [.. Enumerable.Range(0, count).Select(_ => workspace.Sessions[random.Next(workspace.Sessions.Count)].Name)];
        _peers = [.. Enumerable.Range(0, count).Select(_ => $"p{random.Next(peerCount)}")];
        _vectors = [.. Enumerable.Range(0, count).Select(_ =>
        {
            var vector = new float[VectorSpace.Dimensions];
            Corpus.FillUnitVector(random, vector);
            return vector;
        })];
        // Zipf ranks 5 to 15 of 5,000 words: each is in roughly 40 to 70% of 30-to-70-word messages.
        _common = [.. Enumerable.Range(0, count).Select(_ => corpus.WordAt(random.Next(5, 16)))];
        TermSets =
        [
            ("1 rare term", Sets(count, _ => [corpus.WordAt(random.Next(1_000, corpus.VocabularySize))])),
            ("3 terms, 1 common", Sets(count, i => [_common[i], .. Middling(corpus, random, 2)])),
            ("8 terms, 1 common", Sets(count, i => [_common[i], .. Middling(corpus, random, 7)])),
        ];
    }

    public IReadOnlyList<(string Label, IReadOnlyList<IReadOnlyList<string>> Terms)> TermSets { get; }

    public string Text(int index) => _texts[index % _texts.Length];

    public string Session(int index) => _sessions[index % _sessions.Length];

    public string Peer(int index) => _peers[index % _peers.Length];

    public ReadOnlyMemory<float> Vector(int index) => _vectors[index % _vectors.Length];

    public string Common(int index) => _common[index % _common.Length];

    private static IReadOnlyList<IReadOnlyList<string>> Sets(int count, Func<int, IReadOnlyList<string>> make) =>
        [.. Enumerable.Range(0, count).Select(make)];

    private static List<string> Middling(Corpus corpus, Random random, int count) =>
        Enumerable.Range(0, count).Select(_ => corpus.WordAt(random.Next(50, 2_000))).ToList();
}
