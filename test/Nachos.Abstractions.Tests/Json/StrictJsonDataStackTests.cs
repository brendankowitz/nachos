using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nachos.Abstractions.Json;
using Nachos.Testing.Json;
using Shouldly;

namespace Nachos.Abstractions.Tests.Json;

/// <summary>
/// <see cref="StrictJsonData.ToCanonical"/> walks with an explicit stack, so its native stack use is constant whatever
/// the depth. These tests run it at the largest permitted depth on a thread whose stack is far smaller than the
/// default. A regression to recursion cannot be caught: a stack overflow ends the test process, which is how the test
/// then fails.
/// </summary>
public sealed class StrictJsonDataStackTests
{
    private const int SmallStackBytes = 256 * 1024;

    private const int Cap = StrictJsonData.MaxAllowedDepth;

    private static readonly string[] ChainKinds = ["objects", "arrays", "parsed", "element", "mixed"];

    public static IEnumerable<object[]> Chains() => ChainKinds.Select(kind => new object[] { kind });

    [Theory]
    [MemberData(nameof(Chains))]
    public void AtTheCap_IsAccepted_AndOneOverIsRejected_OnASmallStack(string kind)
    {
        // Built bottom-up on this thread, so constructing the input cannot be what overflows.
        var atCap = Build(kind, Cap);
        var overCap = Build(kind, Cap + 1);
        var converter = new CountingMarkerConverter<JsonElement>();

        RunOnSmallStack(() =>
        {
            var canonical = StrictJsonData.ToCanonical(atCap, Cap);

            DepthOf(canonical).ShouldBe(Cap);
            Should.Throw<NachosValidationException>(() => StrictJsonData.ToCanonical(overCap, Cap));
            Should.Throw<NachosValidationException>(() => StrictJsonData.ToCanonical(overCap, StrictJsonData.DefaultMaxDepth));
        });

        converter.Calls.ShouldBe(0);
    }

    [Fact]
    public void ADeepChain_IsRejectedAtTheDefaultLimit_OnASmallStack()
    {
        var deep = Build("objects", 100_000);

        RunOnSmallStack(() => Should.Throw<NachosValidationException>(() => StrictJsonData.ToCanonical(deep)));
    }

    // A filter is parsed at the default limit: writing its canonical tree to text and parsing that must fit too.
    [Fact]
    public void FilterAtItsDepthLimit_IsParsedOnASmallStack()
    {
        var filter = new JsonObject { ["metadata"] = Build("objects", StrictJsonData.DefaultMaxDepth - 1) };

        RunOnSmallStack(() => Filtering.FilterParser.Parse(filter, Filtering.ResourceKind.Workspace).ShouldNotBeNull());
    }

    private static void RunOnSmallStack(Action work)
    {
        Exception? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            },
            maxStackSize: SmallStackBytes);

        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static JsonNode Build(string kind, int containers) => kind switch
    {
        "objects" => Wrap(JsonValue.Create("leaf")!, containers, alternate: false, objects: true),
        "arrays" => Wrap(JsonValue.Create("leaf")!, containers, alternate: false, objects: false),
        "parsed" => JsonNode.Parse(
            string.Concat(Enumerable.Repeat("[", containers)) + "1" + string.Concat(Enumerable.Repeat("]", containers)),
            documentOptions: new JsonDocumentOptions { MaxDepth = containers + 8 })!,
        "element" => ElementChain(containers),
        "mixed" => Wrap(ElementChain(containers / 2), containers - (containers / 2), alternate: true, objects: true),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static JsonValue ElementChain(int containers)
    {
        var json = string.Concat(Enumerable.Repeat("[", containers)) + "1" + string.Concat(Enumerable.Repeat("]", containers));
        var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = containers + 8 });
        return StrictJsonSamples.CustomizedElement(document.RootElement.Clone(), new CountingMarkerConverter<JsonElement>());
    }

    private static JsonNode Wrap(JsonNode inner, int containers, bool alternate, bool objects)
    {
        var node = inner;
        for (var i = 0; i < containers; i++)
        {
            node = objects && (!alternate || i % 2 == 0) ? new JsonObject { ["k"] = node } : new JsonArray(node);
        }

        return node;
    }

    // Counts nested objects and arrays without recursing.
    private static int DepthOf(JsonNode? root)
    {
        var deepest = 0;
        var pending = new Stack<(JsonNode? Node, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            var (node, depth) = pending.Pop();
            switch (node)
            {
                case JsonObject obj:
                    deepest = Math.Max(deepest, depth + 1);
                    foreach (var (_, child) in obj)
                    {
                        pending.Push((child, depth + 1));
                    }

                    break;
                case JsonArray array:
                    deepest = Math.Max(deepest, depth + 1);
                    foreach (var child in array)
                    {
                        pending.Push((child, depth + 1));
                    }

                    break;
            }
        }

        return deepest;
    }
}
