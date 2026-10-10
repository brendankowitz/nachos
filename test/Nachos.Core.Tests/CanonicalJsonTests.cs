using System.Text;
using System.Text.Json;
using Nachos.Core.Idempotency;
using Shouldly;

namespace Nachos.Core.Tests;

public sealed class CanonicalJsonTests
{
    [Theory]
    [InlineData(""" { "array" : [3, null, false, "x"] } """, """{"array":[3,null,false,"x"]}""")]
    [InlineData(""" [ 1e40, 9007199254740993, 1.00, -0 ] """, "[1e40,9007199254740993,1.00,-0]")]
    public void CompactJson_PreservesArraysScalarsAndNumericLexemes(string input, string expected)
    {
        using var document = JsonDocument.Parse(input);

        Encoding.UTF8.GetString(CanonicalJson.Serialize(document.RootElement)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("""{"z":0,"a":{"z":2,"a":1},"A":[{"z":3,"a":4}]}""",
        """{"A":[{"a":4,"z":3}],"a":{"a":1,"z":2},"z":0}""")]
    [InlineData("""{"z":0,"a":1,"a":2}""", """{"a":1,"a":2,"z":0}""")]
    [InlineData("""{"z":"\u0061","a":"a"}""", """{"a":"a","z":"a"}""")]
    [InlineData("""{"unknown":{"z":null,"a":false},"messages":[{"peer_id":"P","configuration":{"reasoning":{"enabled":false}},"content":""}]}""",
        """{"messages":[{"configuration":{"reasoning":{"enabled":false}},"content":"","peer_id":"P"}],"unknown":{"a":false,"z":null}}""")]
    public void CanonicalObjects_AreRecursivelyOrdinalSortedWithoutDroppingFields(string input, string expected)
    {
        using var document = JsonDocument.Parse(input);
        var original = document.RootElement.GetRawText();

        Encoding.UTF8.GetString(CanonicalJson.Serialize(document.RootElement)).ShouldBe(expected);
        document.RootElement.GetRawText().ShouldBe(original);
    }

    [Fact]
    public void NullAndOmissionRemainDifferent()
    {
        using var omitted = JsonDocument.Parse("{}");
        using var explicitNull = JsonDocument.Parse("""{"configuration":null}""");

        CanonicalJson.Serialize(omitted.RootElement).ShouldNotBe(CanonicalJson.Serialize(explicitNull.RootElement));
    }
}
