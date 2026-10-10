using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nachos.Abstractions.Json;

namespace Nachos.Client;

/// <summary>Parses response bodies strictly: duplicate property names are invalid JSON here.</summary>
/// <remarks>
/// <para>
/// A lenient parse would surface a duplicate key later as an <see cref="ArgumentException"/> when the node is
/// read, which is neither a protocol error nor the caller's fault.
/// </para>
/// <para>
/// The parser's own <see cref="JsonException"/> is not surfaced: its text repeats what the server sent (a duplicate
/// property's whole name, which a reflecting server can make the request's bearer value). <see cref="Parse"/> throws a
/// <see cref="JsonException"/> with the fixed text of <see cref="InvalidBodyMessage"/> instead.
/// </para>
/// <para>
/// Depth: strict JSON data (metadata, configuration, filters) may be <see cref="StrictJsonData.DefaultMaxDepth"/> (64)
/// containers deep, and the wire wraps it in an envelope (a page, its <c>items</c> and the entity; a batch, its
/// <c>messages</c> and the entry). <see cref="MaxDepth"/> leaves <see cref="EnvelopeAllowance"/> levels for that on top
/// of the data limit, for both reading responses and writing requests. It stays bounded on purpose: an unbounded
/// depth would let a hostile response cost unbounded recursion.
/// </para>
/// </remarks>
internal static class WireJson
{
    /// <summary>Levels allowed above the data depth limit for the request and response envelopes (3 are used today).</summary>
    public const int EnvelopeAllowance = 16;

    /// <summary>The depth limit for every response the client parses and every request body it serializes.</summary>
    public const int MaxDepth = StrictJsonData.DefaultMaxDepth + EnvelopeAllowance;

    private static readonly JsonDocumentOptions Strict = new() { AllowDuplicateProperties = false, MaxDepth = MaxDepth };

    /// <exception cref="JsonException">
    /// The text is not valid JSON, repeats a property name, or nests past <see cref="MaxDepth"/>. The message is
    /// <see cref="InvalidBodyMessage"/>; it never repeats the body.
    /// </exception>
    public static JsonNode? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json, documentOptions: Strict);
        }
        catch (JsonException)
        {
            throw new JsonException(InvalidBodyMessage);
        }
    }

    /// <summary>The fixed text of a parse failure: no position (a duplicate property reports none) and no body text.</summary>
    public static readonly string InvalidBodyMessage = string.Create(
        CultureInfo.InvariantCulture,
        $"The response body is not valid JSON (malformed, a repeated property name, or nested past {MaxDepth} levels). The parser's own description is withheld because it can repeat what the server sent.");
}
