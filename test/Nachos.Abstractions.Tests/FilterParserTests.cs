using System.Text.Json;
using System.Text.Json.Nodes;
using Nachos.Abstractions.Filtering;
using Nachos.Testing.Json;
using Nachos.Testing.Filtering;
using Shouldly;

namespace Nachos.Abstractions.Tests;

public sealed class FilterParserTests
{
    private static FilterNode? Parse(string json, ResourceKind kind = ResourceKind.Workspace) =>
        FilterParser.Parse(JsonNode.Parse(json), kind);

    private static FilterNode ParseRequired(string json, ResourceKind kind = ResourceKind.Workspace) =>
        Parse(json, kind).ShouldNotBeNull();

    private static JsonNode Value(string json) => JsonNode.Parse(json)!;

    // ------------------------------------------------------------------ top level

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    public void NullOrEmpty_ReturnsNull(string? json) => Parse(json ?? "null").ShouldBeNull();

    [Fact]
    public void NonObjectFilter_Rejected() =>
        Should.Throw<NachosValidationException>(() => Parse("[1]"));

    [Fact]
    public void UnknownTopLevelKey_IsIgnored()
    {
        ParseRequired("""{"bogus":"x"}""").ShouldBeOfType<FilterNode.MatchAll>();

        ParseRequired("""{"bogus":{"nested":[1]},"name":"a"}""")
            .ShouldBe(new FilterNode.Field(FilterColumns.Name, FilterOp.Eq, Value("\"a\"")));
    }

    [Fact]
    public void FieldFromAnotherResource_IsIgnored() =>
        ParseRequired("""{"is_active":"not-a-bool"}""", ResourceKind.Message).ShouldBeOfType<FilterNode.MatchAll>();

    [Fact]
    public void MultipleKeys_AreAnded()
    {
        ParseRequired("""{"name":"a","created_at":null}""").ShouldBe(new FilterNode.And(
        [
            new FilterNode.Field(FilterColumns.Name, FilterOp.Eq, Value("\"a\"")),
            new FilterNode.Field(FilterColumns.CreatedAt, FilterOp.IsNull, null),
        ]));
    }

    [Fact]
    public void WireAliases_MapToCanonicalColumns()
    {
        ColumnOf("""{"id":"a"}""", ResourceKind.Workspace).ShouldBe(FilterColumns.Name);
        ColumnOf("""{"name":"a"}""", ResourceKind.Workspace).ShouldBe(FilterColumns.Name);
        ColumnOf("""{"peer_id":"a"}""", ResourceKind.Peer).ShouldBe(FilterColumns.Name);
        ColumnOf("""{"session_id":"a"}""", ResourceKind.Session).ShouldBe(FilterColumns.Name);
        ColumnOf("""{"peer_id":"a"}""", ResourceKind.Session).ShouldBe(FilterColumns.PeerId);
        ColumnOf("""{"id":"a"}""", ResourceKind.Message).ShouldBe(FilterColumns.PublicId);
        ColumnOf("""{"session_id":"a"}""", ResourceKind.Message).ShouldBe(FilterColumns.SessionId);

        static string ColumnOf(string json, ResourceKind kind) =>
            ParseRequired(json, kind).ShouldBeOfType<FilterNode.Field>().Column;
    }

    // ------------------------------------------------------------------ logical operators

    [Fact]
    public void NotList_IsNotOfOr()
    {
        ParseRequired("""{"NOT":[{"name":"a"},{"name":"b"}]}""").ShouldBe(new FilterNode.Not(
        [
            new FilterNode.Field(FilterColumns.Name, FilterOp.Eq, Value("\"a\"")),
            new FilterNode.Field(FilterColumns.Name, FilterOp.Eq, Value("\"b\"")),
        ]));
    }

    [Fact]
    public void AndOr_NestAndKeepOrder()
    {
        var parsed = ParseRequired("""{"AND":[{"OR":[{"name":"a"},{"name":"b"}]},{"NOT":[{"name":"c"}]}]}""");

        parsed.ShouldBe(new FilterNode.And(
        [
            new FilterNode.Or(
            [
                new FilterNode.Field(FilterColumns.Name, FilterOp.Eq, Value("\"a\"")),
                new FilterNode.Field(FilterColumns.Name, FilterOp.Eq, Value("\"b\"")),
            ]),
            new FilterNode.Not([new FilterNode.Field(FilterColumns.Name, FilterOp.Eq, Value("\"c\""))]),
        ]));
    }

    [Fact]
    public void EmptyLogicalLists_UseTheirIdentity()
    {
        ParseRequired("""{"AND":[]}""").ShouldBeOfType<FilterNode.MatchAll>();
        ParseRequired("""{"OR":[]}""").ShouldBeOfType<FilterNode.MatchNone>();
        ParseRequired("""{"NOT":[]}""").ShouldBeOfType<FilterNode.MatchAll>();
    }

    [Theory]
    [InlineData("""{"AND":{"name":"a"}}""")]
    [InlineData("""{"OR":"a"}""")]
    [InlineData("""{"NOT":null}""")]
    [InlineData("""{"AND":["a"]}""")]
    [InlineData("""{"OR":[[{"name":"a"}]]}""")]
    public void LogicalOperatorsRequireArraysOfObjects_Rejected(string json) =>
        Should.Throw<NachosValidationException>(() => Parse(json));

    [Fact]
    public void LowercaseLogicalKey_IsAnUnknownKey() =>
        ParseRequired("""{"and":[{"name":"a"}]}""").ShouldBeOfType<FilterNode.MatchAll>();

    // ------------------------------------------------------------------ field value shapes

    [Fact]
    public void Scalar_IsEq() =>
        ParseRequired("""{"name":"a"}""").ShouldBe(new FilterNode.Field(FilterColumns.Name, FilterOp.Eq, Value("\"a\"")));

    [Fact]
    public void Null_IsIsNull() =>
        ParseRequired("""{"name":null}""").ShouldBe(new FilterNode.Field(FilterColumns.Name, FilterOp.IsNull, null));

    [Fact]
    public void NeNull_IsNotNull() =>
        ParseRequired("""{"name":{"ne":null}}""")
            .ShouldBe(new FilterNode.Field(FilterColumns.Name, FilterOp.NotNull, null));

    [Fact]
    public void Ne_IsNe() =>
        ParseRequired("""{"name":{"ne":"a"}}""")
            .ShouldBe(new FilterNode.Field(FilterColumns.Name, FilterOp.Ne, Value("\"a\"")));

    [Fact]
    public void Wildcard_IsMatchAll()
    {
        ParseRequired("""{"name":"*"}""").ShouldBeOfType<FilterNode.MatchAll>();
        ParseRequired("""{"name":{"in":["a","*"]}}""").ShouldBeOfType<FilterNode.MatchAll>();
        ParseRequired("""{"name":["*"]}""").ShouldBeOfType<FilterNode.MatchAll>();
        ParseRequired("""{"token_count":"*"}""", ResourceKind.Message).ShouldBeOfType<FilterNode.MatchAll>();
        ParseRequired("""{"is_active":"*"}""", ResourceKind.Session).ShouldBeOfType<FilterNode.MatchAll>();
    }

    [Fact]
    public void BareListOutsideMetadata_IsIn() =>
        ParseRequired("""{"name":["a","b"]}""")
            .ShouldBe(new FilterNode.Field(FilterColumns.Name, FilterOp.In, Value("""["a","b"]""")));

    [Fact]
    public void In_WithNull_AddsIsNullAlternative()
    {
        ParseRequired("""{"name":{"in":["a",null]}}""").ShouldBe(new FilterNode.Or(
        [
            new FilterNode.Field(FilterColumns.Name, FilterOp.In, Value("""["a"]""")),
            new FilterNode.Field(FilterColumns.Name, FilterOp.IsNull, null),
        ]));
        ParseRequired("""{"name":{"in":[null]}}""")
            .ShouldBe(new FilterNode.Field(FilterColumns.Name, FilterOp.IsNull, null));
    }

    [Fact]
    public void EmptyIn_IsMatchNone()
    {
        ParseRequired("""{"name":{"in":[]}}""").ShouldBeOfType<FilterNode.MatchNone>();
        ParseRequired("""{"name":[]}""").ShouldBeOfType<FilterNode.MatchNone>();
    }

    [Fact]
    public void OperatorObject_CombinesOperatorsWithAnd()
    {
        ParseRequired("""{"token_count":{"gte":2,"lt":"9"}}""", ResourceKind.Message).ShouldBe(new FilterNode.And(
        [
            new FilterNode.Field(FilterColumns.TokenCount, FilterOp.Gte, Value("2")),
            new FilterNode.Field(FilterColumns.TokenCount, FilterOp.Lt, Value("9")),
        ]));
    }

    [Fact]
    public void ContainsAndIContains_OnText()
    {
        ParseRequired("""{"content":{"contains":"x","icontains":"Y"}}""", ResourceKind.Message).ShouldBe(
            new FilterNode.And(
            [
                new FilterNode.Field(FilterColumns.Content, FilterOp.Contains, Value("\"x\"")),
                new FilterNode.Field(FilterColumns.Content, FilterOp.IContains, Value("\"Y\"")),
            ]));
    }

    // ------------------------------------------------------------------ typed values

    [Fact]
    public void StringBoolean_Rejected()
    {
        Should.Throw<NachosValidationException>(() => Parse("""{"is_active":"true"}""", ResourceKind.Session));
        Should.Throw<NachosValidationException>(() => Parse("""{"is_active":1}""", ResourceKind.Session));
        Should.Throw<NachosValidationException>(() => Parse("""{"is_active":{"ne":"false"}}""", ResourceKind.Session));

        ParseRequired("""{"is_active":true}""", ResourceKind.Session)
            .ShouldBe(new FilterNode.Field(FilterColumns.IsActive, FilterOp.Eq, Value("true")));
        ParseRequired("""{"is_active":{"ne":false}}""", ResourceKind.Session)
            .ShouldBe(new FilterNode.Field(FilterColumns.IsActive, FilterOp.Ne, Value("false")));
    }

    [Fact]
    public void NumericString_ForTokenCount_Accepted()
    {
        ParseRequired("""{"token_count":"5"}""", ResourceKind.Message)
            .ShouldBe(new FilterNode.Field(FilterColumns.TokenCount, FilterOp.Eq, Value("5")));
        ParseRequired("""{"token_count":5}""", ResourceKind.Message)
            .ShouldBe(new FilterNode.Field(FilterColumns.TokenCount, FilterOp.Eq, Value("5")));
        ParseRequired("""{"token_count":[1,"2"]}""", ResourceKind.Message)
            .ShouldBe(new FilterNode.Field(FilterColumns.TokenCount, FilterOp.In, Value("[1,2]")));

        var field = ParseRequired("""{"token_count":"5"}""", ResourceKind.Message).ShouldBeOfType<FilterNode.Field>();
        field.Value!.GetValue<decimal>().ShouldBe(5m);
    }

    [Fact]
    public void TokenCount_FloatIntegral_Accepted()
    {
        foreach (var json in new[] { "5.0", "5.00", "5e0", "0.5e1", "50e-1" })
        {
            ParseRequired($$$"""{"token_count":{{{json}}}}""", ResourceKind.Message)
                .ShouldBe(new FilterNode.Field(FilterColumns.TokenCount, FilterOp.Eq, Value("5")), json);
        }

        ParseRequired("""{"token_count":1e2}""", ResourceKind.Message)
            .ShouldBe(new FilterNode.Field(FilterColumns.TokenCount, FilterOp.Eq, Value("100")));
        ParseRequired("""{"token_count":"-0"}""", ResourceKind.Message)
            .ShouldBe(new FilterNode.Field(FilterColumns.TokenCount, FilterOp.Eq, Value("0")));
        ParseRequired("""{"token_count":0e-999}""", ResourceKind.Message)
            .ShouldBe(new FilterNode.Field(FilterColumns.TokenCount, FilterOp.Eq, Value("0")));
    }

    [Fact]
    public void TokenCount_HugeInteger_Accepted()
    {
        // Beyond long but inside decimal: kept exactly.
        ParseRequired("""{"token_count":12345678901234567890123}""", ResourceKind.Message)
            .ShouldBe(new FilterNode.Field(FilterColumns.TokenCount, FilterOp.Eq, Value("12345678901234567890123")));
        ParseRequired("""{"token_count":"9223372036854775808"}""", ResourceKind.Message)
            .ShouldBe(new FilterNode.Field(FilterColumns.TokenCount, FilterOp.Eq, Value("9223372036854775808")));

        // Beyond decimal: no stored count can equal it, so the parser folds the comparison.
        string[] beyond = ["1e40", "123456789012345678901234567890123456789", "1e999999999999", "99999999999999999999999999999"];
        foreach (var huge in beyond)
        {
            Folded($$$"""{"token_count":{{{huge}}}}""").ShouldBeOfType<FilterNode.MatchNone>(huge);
            Folded($$$"""{"token_count":{"ne":{{{huge}}}}}""").ShouldBeOfType<FilterNode.MatchAll>(huge);
            Folded($$$"""{"token_count":{"gt":{{{huge}}}}}""").ShouldBeOfType<FilterNode.MatchNone>(huge);
            Folded($$$"""{"token_count":{"gte":{{{huge}}}}}""").ShouldBeOfType<FilterNode.MatchNone>(huge);
            Folded($$$"""{"token_count":{"lt":{{{huge}}}}}""").ShouldBeOfType<FilterNode.MatchAll>(huge);
            Folded($$$"""{"token_count":{"lte":{{{huge}}}}}""").ShouldBeOfType<FilterNode.MatchAll>(huge);
            Folded($$$"""{"token_count":{"gt":-{{{huge}}}}}""").ShouldBeOfType<FilterNode.MatchAll>(huge);
            if (huge.All(char.IsAsciiDigit))
            {
                Folded($$$"""{"token_count":{"lt":"-{{{huge}}}"}}""").ShouldBeOfType<FilterNode.MatchNone>(huge);
            }
        }

        Folded("""{"token_count":{"lte":-1e40}}""").ShouldBeOfType<FilterNode.MatchNone>();

        // In lists drop out-of-range elements and keep the rest.
        Folded("""{"token_count":[1e40]}""").ShouldBeOfType<FilterNode.MatchNone>();
        Folded("""{"token_count":[1e40,7]}""")
            .ShouldBe(new FilterNode.Field(FilterColumns.TokenCount, FilterOp.In, Value("[7]")));

        static FilterNode Folded(string json) => ParseRequired(json, ResourceKind.Message);
    }

    [Theory]
    [InlineData("""{"token_count":5.5}""")]
    [InlineData("""{"token_count":1e-1}""")]
    [InlineData("""{"token_count":0.0000000000000000000000000000001}""")]
    [InlineData("""{"token_count":100000000000000000000000000000000000000.5}""")]
    [InlineData("""{"token_count":"5.0"}""")]
    [InlineData("""{"token_count":"1e2"}""")]
    [InlineData("""{"token_count":{"gt":2.5}}""")]
    public void TokenCount_NonIntegral_Rejected(string json) =>
        Should.Throw<NachosValidationException>(() => Parse(json, ResourceKind.Message));

    [Theory]
    [InlineData("""{"token_count":"abc"}""")]
    [InlineData("""{"token_count":"5.5"}""")]
    [InlineData("""{"token_count":" 5"}""")]
    [InlineData("""{"token_count":true}""")]
    [InlineData("""{"token_count":[1,"x"]}""")]
    public void BadTokenCount_Rejected(string json) =>
        Should.Throw<NachosValidationException>(() => Parse(json, ResourceKind.Message));

    [Fact]
    public void DateOnly_IsUtcMidnight()
    {
        var field = ParseRequired("""{"created_at":{"gte":"2026-01-02"}}""").ShouldBeOfType<FilterNode.Field>();

        field.Op.ShouldBe(FilterOp.Gte);
        var value = field.Value!.GetValue<DateTimeOffset>();
        value.ShouldBe(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        value.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("2026-01-03T13:00:00+01:00", "2026-01-03T12:00:00Z")]
    [InlineData("2026-01-03T12:00:00Z", "2026-01-03T12:00:00Z")]
    [InlineData("2026-01-03T12:00:00", "2026-01-03T12:00:00Z")]
    [InlineData("2026-01-03T12:00", "2026-01-03T12:00:00Z")]
    [InlineData("2026-01-03T12:00:00.250Z", "2026-01-03T12:00:00.250Z")]
    public void Timestamps_NormalizeToUtc(string input, string expected)
    {
        var field = ParseRequired($$"""{"created_at":"{{input}}"}""").ShouldBeOfType<FilterNode.Field>();

        var value = field.Value!.GetValue<DateTimeOffset>();
        value.ShouldBe(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
        value.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("""{"created_at":"yesterday"}""")]
    [InlineData("""{"created_at":"01/02/2026"}""")]
    [InlineData("""{"created_at":"2026-13-01"}""")]
    [InlineData("""{"created_at":5}""")]
    [InlineData("""{"created_at":true}""")]
    [InlineData("""{"created_at":{"gt":null}}""")]
    public void BadTimestamp_Rejected(string json) =>
        Should.Throw<NachosValidationException>(() => Parse(json));

    [Theory]
    [InlineData("""{"name":5}""")]
    [InlineData("""{"name":true}""")]
    [InlineData("""{"name":["a",5]}""")]
    [InlineData("""{"name":{"contains":5}}""")]
    [InlineData("""{"name":{"contains":null}}""")]
    [InlineData("""{"name":{"in":[{"x":1}]}}""")]
    public void TextRequiresStrings_Rejected(string json) =>
        Should.Throw<NachosValidationException>(() => Parse(json));

    // ------------------------------------------------------------------ operators

    [Theory]
    [InlineData("""{"name":{"regex":"a"}}""")]
    [InlineData("""{"name":{"eq":"a"}}""")]
    [InlineData("""{"name":{"GT":"a"}}""")]
    public void UnknownOperator_Rejected(string json) =>
        Should.Throw<NachosValidationException>(() => Parse(json));

    [Theory]
    [InlineData("""{"name":{"gt":"a"}}""")]
    [InlineData("""{"name":{"lte":"a"}}""")]
    [InlineData("""{"created_at":{"contains":"2026"}}""")]
    [InlineData("""{"created_at":{"icontains":"2026"}}""")]
    public void OperatorNotAllowedForType_Rejected(string json) =>
        Should.Throw<NachosValidationException>(() => Parse(json));

    [Theory]
    [InlineData("""{"is_active":{"gt":false}}""")]
    [InlineData("""{"is_active":{"in":[true]}}""")]
    [InlineData("""{"is_active":[true,false]}""")]
    [InlineData("""{"is_active":{"contains":true}}""")]
    public void OperatorNotAllowedForBool_Rejected(string json) =>
        Should.Throw<NachosValidationException>(() => Parse(json, ResourceKind.Session));

    [Theory]
    [InlineData("""{"token_count":{"contains":"5"}}""")]
    [InlineData("""{"token_count":{"icontains":"5"}}""")]
    public void ContainsNotAllowedForNumbers_Rejected(string json) =>
        Should.Throw<NachosValidationException>(() => Parse(json, ResourceKind.Message));

    [Theory]
    [InlineData("""{"name":{}}""")]
    [InlineData("""{"name":{"in":"a"}}""")]
    [InlineData("""{"name":{"in":null}}""")]
    [InlineData("""{"name":{"gt":null}}""")]
    public void MalformedOperatorObject_Rejected(string json) =>
        Should.Throw<NachosValidationException>(() => Parse(json));

    [Fact]
    public void ValidationError_CarriesStringDetail()
    {
        var error = Should.Throw<NachosValidationException>(() => Parse("""{"name":{"regex":"a"}}"""));

        error.Detail.ShouldNotBeNullOrWhiteSpace();
        error.Detail.ShouldContain("regex");
    }

    // ------------------------------------------------------------------ metadata

    [Fact]
    public void MetadataBareObject_IsPerKeyEq()
    {
        ParseRequired("""{"metadata":{"env":"prod","n":1,"flag":true}}""").ShouldBe(new FilterNode.And(
        [
            new FilterNode.MetadataPath(["env"], FilterOp.Eq, Value("\"prod\"")),
            new FilterNode.MetadataPath(["n"], FilterOp.Eq, Value("1")),
            new FilterNode.MetadataPath(["flag"], FilterOp.Eq, Value("true")),
        ]));
    }

    [Fact]
    public void MetadataNestedObjects_RecurseIntoPath() =>
        ParseRequired("""{"metadata":{"profile":{"dept":{"name":"eng"}}}}""")
            .ShouldBe(new FilterNode.MetadataPath(["profile", "dept", "name"], FilterOp.Eq, Value("\"eng\"")));

    [Fact]
    public void BareListInsideMetadata_IsJsonContains() =>
        ParseRequired("""{"metadata":{"tags":["a","b"]}}""")
            .ShouldBe(new FilterNode.MetadataPath(["tags"], FilterOp.JsonContains, Value("""["a","b"]""")));

    [Fact]
    public void MetadataEmptyList_Rejected() =>
        Should.Throw<NachosValidationException>(() => Parse("""{"metadata":{"tags":[]}}"""));

    [Fact]
    public void MetadataKeyOperators_GiveMetadataPaths()
    {
        ParseRequired("""{"metadata":{"score":{"gte":4,"lte":5.5}}}""").ShouldBe(new FilterNode.And(
        [
            new FilterNode.MetadataPath(["score"], FilterOp.Gte, Value("4")),
            new FilterNode.MetadataPath(["score"], FilterOp.Lte, Value("5.5")),
        ]));
        ParseRequired("""{"metadata":{"a":{"b":{"ne":"x"}}}}""")
            .ShouldBe(new FilterNode.MetadataPath(["a", "b"], FilterOp.Ne, Value("\"x\"")));
        ParseRequired("""{"metadata":{"k":{"ne":null}}}""")
            .ShouldBe(new FilterNode.MetadataPath(["k"], FilterOp.NotNull, null));
        ParseRequired("""{"metadata":{"k":{"contains":"x"}}}""")
            .ShouldBe(new FilterNode.MetadataPath(["k"], FilterOp.Contains, Value("\"x\"")));
        ParseRequired("""{"metadata":{"k":{"icontains":"x"}}}""")
            .ShouldBe(new FilterNode.MetadataPath(["k"], FilterOp.IContains, Value("\"x\"")));
    }

    [Fact]
    public void MetadataIn_IsOrOfEquality()
    {
        ParseRequired("""{"metadata":{"k":{"in":["a",2]}}}""").ShouldBe(new FilterNode.Or(
        [
            new FilterNode.MetadataPath(["k"], FilterOp.Eq, Value("\"a\"")),
            new FilterNode.MetadataPath(["k"], FilterOp.Eq, Value("2")),
        ]));
        ParseRequired("""{"metadata":{"k":{"in":["a"]}}}""")
            .ShouldBe(new FilterNode.MetadataPath(["k"], FilterOp.Eq, Value("\"a\"")));
        ParseRequired("""{"metadata":{"k":{"in":[]}}}""").ShouldBeOfType<FilterNode.MatchNone>();
        ParseRequired("""{"metadata":{"k":{"in":["a","*"]}}}""")
            .ShouldBe(new FilterNode.MetadataPath(["k"], FilterOp.NotNull, null));
    }

    [Fact]
    public void MetadataWildcard_MeansKeyExists()
    {
        ParseRequired("""{"metadata":{"k":"*"}}""")
            .ShouldBe(new FilterNode.MetadataPath(["k"], FilterOp.NotNull, null));
        ParseRequired("""{"metadata":"*"}""").ShouldBeOfType<FilterNode.MatchAll>();
    }

    [Fact]
    public void MetadataNull_IsIsNull() =>
        ParseRequired("""{"metadata":{"k":null}}""")
            .ShouldBe(new FilterNode.MetadataPath(["k"], FilterOp.IsNull, null));

    [Fact]
    public void MetadataContainsWrapper_EqualsBareObject()
    {
        var wrapped = ParseRequired("""{"metadata":{"contains":{"a":"1","b":{"c":[2]}}}}""");
        var bare = ParseRequired("""{"metadata":{"a":"1","b":{"c":[2]}}}""");

        wrapped.ShouldBe(bare);
    }

    [Fact]
    public void MetadataEmptyObject_IsMatchAll() =>
        ParseRequired("""{"metadata":{}}""").ShouldBeOfType<FilterNode.MatchAll>();

    [Theory]
    [InlineData("""{"metadata":{"ne":{}}}""")]
    [InlineData("""{"metadata":{"ne":"x"}}""")]
    [InlineData("""{"metadata":{"gt":1}}""")]
    [InlineData("""{"metadata":{"in":["a"]}}""")]
    [InlineData("""{"metadata":{"icontains":{"a":"b"}}}""")]
    [InlineData("""{"metadata":{"contains":"x"}}""")]
    public void MetadataWholeObjectOperator_Rejected(string json) =>
        Should.Throw<NachosValidationException>(() => Parse(json));

    [Theory]
    [InlineData("""{"metadata":"x"}""")]
    [InlineData("""{"metadata":5}""")]
    [InlineData("""{"metadata":["a"]}""")]
    [InlineData("""{"metadata":true}""")]
    public void MetadataNotAnObject_Rejected(string json) =>
        Should.Throw<NachosValidationException>(() => Parse(json));

    [Fact]
    public void MetadataNull_IsMatchNone()
    {
        ParseRequired("""{"metadata":null}""").ShouldBeOfType<FilterNode.MatchNone>();
        ParseRequired("""{"metadata":null}""", ResourceKind.Message).ShouldBeOfType<FilterNode.MatchNone>();
    }

    [Fact]
    public void MetadataKeyEmptyObject_Rejected()
    {
        Should.Throw<NachosValidationException>(() => Parse("""{"metadata":{"k":{}}}"""));
        Should.Throw<NachosValidationException>(() => Parse("""{"metadata":{"a":{"b":{}}}}"""));
    }

    [Theory]
    [InlineData("""{"metadata":{"k":[null]}}""")]
    [InlineData("""{"metadata":{"k":[{"a":1}]}}""")]
    [InlineData("""{"metadata":{"k":[[1]]}}""")]
    [InlineData("""{"metadata":{"k":["a",null]}}""")]
    [InlineData("""{"metadata":{"k":{"in":[[1]]}}}""")]
    public void MetadataListElementsMustBeScalars_Rejected(string json) =>
        Should.Throw<NachosValidationException>(() => Parse(json));

    [Fact]
    public void MetadataListElementsKeepTheirKind() =>
        ParseRequired("""{"metadata":{"k":[1,"1",true,"true",1.0]}}""")
            .ShouldBe(new FilterNode.MetadataPath(["k"], FilterOp.JsonContains, Value("""[1,"1",true,"true",1.0]""")));

    [Fact]
    public void MetadataKeysAreLiteral() =>
        ParseRequired("""{"metadata":{"a.b":1,"q\"uote":2,"k[0]":"*"}}""").ShouldBe(new FilterNode.And(
        [
            new FilterNode.MetadataPath(["a.b"], FilterOp.Eq, Value("1")),
            new FilterNode.MetadataPath(["q\"uote"], FilterOp.Eq, Value("2")),
            new FilterNode.MetadataPath(["k[0]"], FilterOp.NotNull, null),
        ]));

    [Theory]
    [InlineData("""{"metadata":{"k":{"gte":1,"bogus":2}}}""")]
    [InlineData("""{"metadata":{"k":{"gte":true}}}""")]
    [InlineData("""{"metadata":{"k":{"gte":null}}}""")]
    [InlineData("""{"metadata":{"k":{"in":"a"}}}""")]
    [InlineData("""{"metadata":{"k":{"in":[{"x":1}]}}}""")]
    [InlineData("""{"metadata":{"k":{"ne":[1]}}}""")]
    [InlineData("""{"metadata":{"k":{"contains":1}}}""")]
    public void MalformedMetadataOperators_Rejected(string json) =>
        Should.Throw<NachosValidationException>(() => Parse(json));

    // ------------------------------------------------------------------ input normalization

    [Fact]
    public void CSharpBuiltFilter_IntTokenCount_Accepted()
    {
        var filter = new JsonObject { ["token_count"] = 5 };

        FilterParser.Parse(filter, ResourceKind.Message)
            .ShouldBe(new FilterNode.Field(FilterColumns.TokenCount, FilterOp.Eq, Value("5")));
    }

    [Fact]
    public void CSharpBuiltFilter_DateTimeOffsetValue_Accepted()
    {
        var filter = new JsonObject
        {
            ["created_at"] = new JsonObject { ["gte"] = JsonValue.Create(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2))) },
        };

        var field = FilterParser.Parse(filter, ResourceKind.Workspace).ShouldBeOfType<FilterNode.Field>();

        field.Value!.GetValue<DateTimeOffset>().ShouldBe(new DateTimeOffset(2026, 1, 2, 1, 4, 5, TimeSpan.Zero));
    }

    [Fact]
    public void CSharpBuiltFilter_GuidValue_TreatedAsString()
    {
        var id = Guid.NewGuid();

        FilterParser.Parse(new JsonObject { ["name"] = JsonValue.Create(id) }, ResourceKind.Workspace)
            .ShouldBe(new FilterNode.Field(FilterColumns.Name, FilterOp.Eq, Value($"\"{id}\"")));
        FilterParser.Parse(new JsonObject { ["content"] = JsonValue.Create('x') }, ResourceKind.Message)
            .ShouldBe(new FilterNode.Field(FilterColumns.Content, FilterOp.Eq, Value("\"x\"")));
    }

    [Fact]
    public void CSharpBuiltFilter_DoesNotMutateOrShareTheInput()
    {
        var filter = JsonNode.Parse("""{"metadata":{"tags":["a"]}}""")!;
        var before = filter.ToJsonString();

        var parsed = FilterParser.Parse(filter, ResourceKind.Workspace).ShouldBeOfType<FilterNode.MetadataPath>();
        parsed.Value!.AsArray()[0] = "changed";

        filter.ToJsonString().ShouldBe(before);
    }

    [Fact]
    public void DuplicateKeys_Rejected422()
    {
        Should.Throw<NachosValidationException>(() => FilterParser.Parse("""{"name":"a","name":"b"}""", ResourceKind.Workspace));
        Should.Throw<NachosValidationException>(() => FilterParser.Parse("""{"metadata":{"k":1,"k":2}}""", ResourceKind.Workspace));
        Should.Throw<NachosValidationException>(() => FilterParser.Parse("""{"AND":[{"name":"a","name":"a"}]}""", ResourceKind.Workspace));
        Should.Throw<NachosValidationException>(() => FilterParser.Parse("""{"unknown":{"x":1,"x":2}}""", ResourceKind.Workspace));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{"name":}""")]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("\"x\"")]
    public void MalformedJsonText_Rejected422(string json) =>
        Should.Throw<NachosValidationException>(() => FilterParser.Parse(json, ResourceKind.Workspace));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("null")]
    [InlineData("{}")]
    public void StringOverload_BlankNullOrEmpty_ReturnsNull(string? json) =>
        FilterParser.Parse(json, ResourceKind.Workspace).ShouldBeNull();

    [Fact]
    public void StringOverload_ParsesLikeTheNodeOverload() =>
        FilterParser.Parse("""{"name":"a"}""", ResourceKind.Workspace)
            .ShouldBe(FilterParser.Parse(JsonNode.Parse("""{"name":"a"}"""), ResourceKind.Workspace));

    [Theory]
    [InlineData("""{"\uD800":1}""")]
    [InlineData("""{"name":"\uD800"}""")]
    [InlineData("""{"metadata":{"\uDC00":1}}""")]
    [InlineData("""{"name":["a","\uDC00"]}""")]
    [InlineData("""{"name":{"in":["\uD800"]}}""")]
    [InlineData("""{"metadata":{"k":"\uD800"}}""")]
    public void Parse_LoneSurrogate_Rejected422(string json)
    {
        Should.Throw<NachosValidationException>(() => FilterParser.Parse(json, ResourceKind.Workspace));
        Should.Throw<NachosValidationException>(() => FilterParser.Parse(json, ResourceKind.Message));
    }

    [Fact]
    public void Parse_ValidSurrogatePair_Accepted() =>
        ParseRequired("""{"name":"\uD83D\uDE00"}""")
            .ShouldBe(new FilterNode.Field(FilterColumns.Name, FilterOp.Eq, JsonValue.Create("\U0001F600")));

    [Fact]
    public void CSharpBuiltFilter_NaN_Rejected422()
    {
        Should.Throw<NachosValidationException>(() => FilterParser.Parse(
            new JsonObject { ["metadata"] = new JsonObject { ["k"] = JsonValue.Create(double.NaN) } }, ResourceKind.Workspace));
        Should.Throw<NachosValidationException>(() => FilterParser.Parse(
            new JsonObject { ["token_count"] = JsonValue.Create(double.PositiveInfinity) }, ResourceKind.Message));
        Should.Throw<NachosValidationException>(() => FilterParser.Parse(
            new JsonObject { ["metadata"] = new JsonObject { ["k"] = JsonValue.Create(double.NegativeInfinity) } }, ResourceKind.Workspace));
    }

    [Fact]
    public void ConstructedFilter_LoneHighSurrogateValue_Rejected422() =>
        Should.Throw<NachosValidationException>(() =>
            FilterParser.Parse(new JsonObject { ["content"] = "a\uD800b" }, ResourceKind.Message));

    [Fact]
    public void ConstructedFilter_LoneLowSurrogateValue_Rejected422()
    {
        Should.Throw<NachosValidationException>(() =>
            FilterParser.Parse(new JsonObject { ["content"] = "\uDC00" }, ResourceKind.Message));
        Should.Throw<NachosValidationException>(() =>
            FilterParser.Parse(new JsonObject { ["content"] = "\uDE00\uD83D" }, ResourceKind.Message));
        Should.Throw<NachosValidationException>(() =>
            FilterParser.Parse(new JsonObject { ["content"] = "x\uD83D" }, ResourceKind.Message));
    }

    [Fact]
    public void ConstructedFilter_LoneSurrogateMetadataKey_Rejected422()
    {
        Should.Throw<NachosValidationException>(() => FilterParser.Parse(
            new JsonObject { ["metadata"] = new JsonObject { ["\uD800"] = 1 } }, ResourceKind.Workspace));
        Should.Throw<NachosValidationException>(() => FilterParser.Parse(
            new JsonObject { ["unknown\uDC00"] = 1 }, ResourceKind.Workspace));
    }

    [Fact]
    public void ConstructedFilter_LoneSurrogateInNestedArray_Rejected422()
    {
        Should.Throw<NachosValidationException>(() => FilterParser.Parse(
            new JsonObject { ["AND"] = new JsonArray(new JsonObject { ["name"] = new JsonArray("a", "\uD800") }) },
            ResourceKind.Workspace));
        Should.Throw<NachosValidationException>(() => FilterParser.Parse(
            new JsonObject { ["metadata"] = new JsonObject { ["tags"] = new JsonArray("ok", "\uDC00") } },
            ResourceKind.Workspace));
    }

    [Fact]
    public void ConstructedFilter_LoneSurrogateCharValue_Rejected422()
    {
        Should.Throw<NachosValidationException>(() => FilterParser.Parse(
            new JsonObject { ["content"] = JsonValue.Create('\uD800') }, ResourceKind.Message));
        Should.Throw<NachosValidationException>(() => FilterParser.Parse(
            new JsonObject { ["content"] = JsonValue.Create('\uDC00') }, ResourceKind.Message));
    }

    [Fact]
    public void ConstructedFilter_ValidSurrogatePair_Accepted() =>
        FilterParser.Parse(new JsonObject { ["content"] = "\uD83D\uDE00" }, ResourceKind.Message)
            .ShouldBe(new FilterNode.Field(FilterColumns.Content, FilterOp.Eq, JsonValue.Create("\U0001F600")));

    [Fact]
    public void ConstructedFilter_GenuineReplacementChar_Accepted() =>
        FilterParser.Parse(new JsonObject { ["content"] = "\uFFFD" }, ResourceKind.Message)
            .ShouldBe(new FilterNode.Field(FilterColumns.Content, FilterOp.Eq, JsonValue.Create("\uFFFD")));

    [Fact]
    public void ConstructedFilter_DepthOver64_Rejected422()
    {
        JsonNode node = new JsonObject();
        for (var i = 0; i < 100; i++)
        {
            node = new JsonObject { ["AND"] = new JsonArray(node) };
        }

        Should.Throw<NachosValidationException>(() => FilterParser.Parse(node, ResourceKind.Workspace));
    }

    // JsonNode.Parse (without AllowDuplicateProperties = false) accepts duplicate keys and escaped lone surrogates in
    // keys; they only surface when the object is enumerated or serialized, which must still be a 422.

    [Fact]
    public void NodeOverload_DuplicateKeysRoot_Rejected422() =>
        Should.Throw<NachosValidationException>(() =>
            FilterParser.Parse(JsonNode.Parse("""{"content":"a","content":"b"}"""), ResourceKind.Message));

    [Fact]
    public void NodeOverload_DuplicateKeysNested_Rejected422()
    {
        Should.Throw<NachosValidationException>(() =>
            FilterParser.Parse(JsonNode.Parse("""{"metadata":{"k":1,"k":2}}"""), ResourceKind.Workspace));
        Should.Throw<NachosValidationException>(() =>
            FilterParser.Parse(JsonNode.Parse("""{"AND":[{"name":"a","name":"b"}]}"""), ResourceKind.Workspace));
        Should.Throw<NachosValidationException>(() =>
            FilterParser.Parse(JsonNode.Parse("""{"name":{"in":["a"],"in":["b"]}}"""), ResourceKind.Workspace));
    }

    [Fact]
    public void NodeOverload_DuplicateKeysInsideUnknownField_Rejected422()
    {
        Should.Throw<NachosValidationException>(() =>
            FilterParser.Parse(JsonNode.Parse("""{"unknown":{"x":1,"x":2}}"""), ResourceKind.Workspace));
        Should.Throw<NachosValidationException>(() =>
            FilterParser.Parse(JsonNode.Parse("""{"unknown":[{"x":{"y":1,"y":2}}]}"""), ResourceKind.Workspace));
    }

    [Fact]
    public void NodeOverload_JsonBackedEscapedSurrogateKey_Rejected422()
    {
        Should.Throw<NachosValidationException>(() =>
            FilterParser.Parse(JsonNode.Parse("""{"\uD800":1}"""), ResourceKind.Workspace));
        Should.Throw<NachosValidationException>(() =>
            FilterParser.Parse(JsonNode.Parse("""{"metadata":{"\uDC00":1}}"""), ResourceKind.Workspace));
        Should.Throw<NachosValidationException>(() =>
            FilterParser.Parse(JsonNode.Parse("""{"unknown":{"\uD800":1}}"""), ResourceKind.Workspace));
        Should.Throw<NachosValidationException>(() =>
            FilterParser.Parse(JsonNode.Parse("""{"content":"\uD800"}"""), ResourceKind.Message));
    }

    // JsonDocumentOptions.MaxDepth = 64 counts objects and arrays only: 64 nested containers are accepted, 65 are not,
    // and a scalar or null leaf adds no level. Both overloads must agree.
    public static TheoryData<int, bool, bool> DepthCases()
    {
        var data = new TheoryData<int, bool, bool>();
        for (var depth = 63; depth <= 65; depth++)
        {
            data.Add(depth, false, false);
            data.Add(depth, false, true);
            data.Add(depth, true, false);
            data.Add(depth, true, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DepthCases))]
    public void DepthParity_63_64_65_BothOverloads(int containers, bool nodeOverload, bool nullLeaf)
    {
        // The root object plus (containers - 1) nested metadata objects.
        Func<FilterNode?> parse;
        if (nodeOverload)
        {
            JsonNode? node = nullLeaf ? null : JsonValue.Create("ok");
            for (var i = 0; i < containers - 1; i++)
            {
                node = new JsonObject { ["k"] = node };
            }

            var filter = new JsonObject { ["metadata"] = node };
            parse = () => FilterParser.Parse(filter, ResourceKind.Workspace);
        }
        else
        {
            var json = """{"metadata":""" + string.Concat(Enumerable.Repeat("""{"k":""", containers - 1))
                + (nullLeaf ? "null" : "\"ok\"") + new string('}', containers - 1) + "}";
            parse = () => FilterParser.Parse(json, ResourceKind.Workspace);
        }

        if (containers <= 64)
        {
            parse().ShouldNotBeNull();
        }
        else
        {
            Should.Throw<NachosValidationException>(parse);
        }
    }

    [Theory]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    public void DepthParity_ArrayAsInnermostContainer(int containers)
    {
        // The array is the innermost container, so the root and (containers - 2) objects enclose it.
        JsonNode node = new JsonArray("ok");
        for (var i = 0; i < containers - 2; i++)
        {
            node = new JsonObject { ["k"] = node };
        }

        var filter = new JsonObject { ["metadata"] = node };
        var json = """{"metadata":""" + string.Concat(Enumerable.Repeat("""{"k":""", containers - 2))
            + """["ok"]""" + new string('}', containers - 2) + "}";

        if (containers <= 64)
        {
            FilterParser.Parse(filter, ResourceKind.Workspace).ShouldBe(FilterParser.Parse(json, ResourceKind.Workspace));
        }
        else
        {
            Should.Throw<NachosValidationException>(() => FilterParser.Parse(json, ResourceKind.Workspace));
            Should.Throw<NachosValidationException>(() => FilterParser.Parse(filter, ResourceKind.Workspace));
        }
    }

    // ------------------------------------------------------------------ strict JSON-data inputs

    private static FilterNode.MetadataPath ParseMetadataValue(JsonNode? value) =>
        FilterParser.Parse(new JsonObject { ["metadata"] = new JsonObject { ["k"] = value } }, ResourceKind.Workspace)
            .ShouldBeOfType<FilterNode.MetadataPath>();

    private static void RejectedBecauseOfType(JsonNode? value, Type backingType)
    {
        var ex = Should.Throw<NachosValidationException>(() =>
            FilterParser.Parse(new JsonObject { ["metadata"] = new JsonObject { ["k"] = value } }, ResourceKind.Workspace));
        ex.Message.ShouldContain(backingType.ToString());
        ex.Message.ShouldContain("JsonObject");
        ex.Message.ShouldContain("JsonArray");
        ex.Message.ShouldContain("JsonSerializer.SerializeToNode");
    }

    [Fact]
    public void InterfaceProjection_Rejected422_WithoutRunningCallerCode()
    {
        var projection = new NameProjection();

        RejectedBecauseOfType(StrictJsonSamples.InterfaceProjection(projection), typeof(NameProjection));

        projection.ExcludedGetterCalls.ShouldBe(0);
    }

    [Fact]
    public void ExtensionDataClass_WithLoneSurrogateKey_Rejected422AsAType()
    {
        var ex = Should.Throw<NachosValidationException>(() => ParseMetadataValue(JsonValue.Create(new WithExtensionData())));

        ex.Message.ShouldContain(typeof(WithExtensionData).ToString());
        ex.Message.ShouldNotContain("surrogate");
    }

    [Fact]
    public void ScalarWithCustomConverter_ConverterIgnored_LiteralValueUsed()
    {
        var converter = new CountingUppercaseConverter();

        var parsed = FilterParser.Parse(
            new JsonObject { ["name"] = StrictJsonSamples.UppercasedString("abc", converter) }, ResourceKind.Workspace);

        parsed.ShouldBe(new FilterNode.Field(FilterColumns.Name, FilterOp.Eq, Value("\"abc\"")));
        converter.Calls.ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(StrictJsonSamples.AllowedScalars), MemberType = typeof(StrictJsonSamples))]
    public void AllowedScalar_RoundTripsIntoTheExpectedFilterValue(string kind, string expectedJson)
    {
        var parsed = ParseMetadataValue(StrictJsonSamples.Scalar(kind));

        JsonNode.DeepEquals(parsed.Value, Value(expectedJson)).ShouldBeTrue(parsed.Value?.ToJsonString());
    }

    [Fact]
    public void JsonElementBackedString_WithEscapedLoneSurrogate_Rejected422()
    {
        using var document = JsonDocument.Parse("\"\\uD800\"");

        Should.Throw<NachosValidationException>(() => ParseMetadataValue(JsonValue.Create(document.RootElement)));
    }

    [Fact]
    public void ConstructedFilter_NonFiniteFloat_Rejected422()
    {
        Should.Throw<NachosValidationException>(() => ParseMetadataValue(JsonValue.Create(float.NaN)));
        Should.Throw<NachosValidationException>(() => ParseMetadataValue(JsonValue.Create(float.PositiveInfinity)));
        Should.Throw<NachosValidationException>(() => ParseMetadataValue(JsonValue.Create(float.NegativeInfinity)));
    }

    [Theory]
    [MemberData(nameof(StrictJsonSamples.DisallowedKinds), MemberType = typeof(StrictJsonSamples))]
    public void DisallowedBackingType_Rejected422_NamingTheType(string kind)
    {
        var (value, type) = StrictJsonSamples.Disallowed(kind);

        RejectedBecauseOfType(value, type);
    }

    [Fact]
    public void DisallowedBackingType_AsRoot_Rejected422() =>
        Should.Throw<NachosValidationException>(() =>
            FilterParser.Parse(JsonValue.Create(new List<int> { 1 }), ResourceKind.Workspace));

    [Fact]
    public void TypedComposite_HoldingTheArrayThatHoldsIt_Rejected422AsAType()
    {
        // A typed list holding the array that holds it: rejected up front, not walked.
        var list = new List<JsonNode>();
        var array = new JsonArray(JsonValue.Create(list));
        list.Add(array);

        var ex = Should.Throw<NachosValidationException>(() =>
            FilterParser.Parse(new JsonObject { ["unknown"] = array }, ResourceKind.Workspace));
        ex.Message.ShouldContain(typeof(List<JsonNode>).ToString());
    }

    [Fact]
    public void LoneSurrogate_InStringCharAndKey_Rejected422()
    {
        Should.Throw<NachosValidationException>(() => FilterParser.Parse(
            new JsonObject { ["content"] = JsonValue.Create<string>("a\uD800") }, ResourceKind.Message));
        Should.Throw<NachosValidationException>(() => FilterParser.Parse(
            new JsonObject { ["content"] = JsonValue.Create('\uDC00') }, ResourceKind.Message));
        Should.Throw<NachosValidationException>(() => FilterParser.Parse(
            new JsonObject { ["metadata"] = new JsonObject { ["k\uD800"] = 1 } }, ResourceKind.Workspace));
    }

    [Fact]
    public void ArraysAndObjects_WithValidSurrogatesAndReplacementChars_Accepted()
    {
        FilterParser.Parse(new JsonObject { ["content"] = new JsonArray("a", "\U0001F600", "\uFFFD") }, ResourceKind.Message)
            .ShouldBe(FilterParser.Parse("""{"content":["a","\uD83D\uDE00","\uFFFD"]}""", ResourceKind.Message));
        FilterParser.Parse(new JsonObject { ["token_count"] = new JsonArray(1, 2) }, ResourceKind.Message)
            .ShouldBe(FilterParser.Parse("""{"token_count":[1,2]}""", ResourceKind.Message));
        FilterParser.Parse(
                new JsonObject { ["metadata"] = new JsonObject { ["k"] = "v", ["\U0001F600"] = new JsonArray("a") } },
                ResourceKind.Workspace)
            .ShouldBe(FilterParser.Parse("""{"metadata":{"k":"v","\uD83D\uDE00":["a"]}}""", ResourceKind.Workspace));
    }

    [Fact]
    public void ArraysAndObjects_WithLoneSurrogates_Rejected422()
    {
        Should.Throw<NachosValidationException>(() => FilterParser.Parse(
            new JsonObject { ["content"] = new JsonArray("ok", "\uD800") }, ResourceKind.Message));
        Should.Throw<NachosValidationException>(() => FilterParser.Parse(
            new JsonObject { ["content"] = new JsonObject { ["in"] = new JsonArray("ok", '\uD800') } }, ResourceKind.Message));
        Should.Throw<NachosValidationException>(() => FilterParser.Parse(
            new JsonObject { ["metadata"] = new JsonObject { ["ok"] = new JsonObject { ["\uDC00"] = 1 } } },
            ResourceKind.Workspace));
        Should.Throw<NachosValidationException>(() => FilterParser.Parse(
            new JsonObject { ["metadata"] = new JsonObject { ["k"] = new JsonArray("x\uD800") } }, ResourceKind.Workspace));
    }

    // ------------------------------------------------------------------ node overload equals text overload

    // Every shared case, fed as text, as a parsed node, as a node rebuilt from CLR-typed scalars and as the library's
    // own node, must give the same filter or the same kind of failure.
    [Fact]
    public void NodeOverload_EqualsTextOverload_AcrossTheSharedCases()
    {
        var mismatches = new List<string>();

        foreach (var filterCase in FilterCaseLibrary.All)
        {
            var text = filterCase.Filter?.ToJsonString() ?? "null";
            var results = new[]
            {
                Outcome(() => FilterParser.Parse(text, filterCase.Resource)),
                Outcome(() => FilterParser.Parse(JsonNode.Parse(text), filterCase.Resource)),
                Outcome(() => FilterParser.Parse(Rebuild(JsonNode.Parse(text)), filterCase.Resource)),
                Outcome(() => FilterParser.Parse(filterCase.Filter, filterCase.Resource)),
            };

            if (results.Any(r => !SameOutcome(results[0], r)))
            {
                mismatches.Add($"{filterCase.Name}: {string.Join(" | ", results.Select(r => r.Failure ?? r.Filter?.ToString() ?? "null"))}");
            }
        }

        FilterCaseLibrary.All.Count.ShouldBeGreaterThan(200);
        mismatches.ShouldBeEmpty();
    }

    private static (FilterNode? Filter, string? Failure) Outcome(Func<FilterNode?> parse)
    {
        try
        {
            return (parse(), null);
        }
        catch (NachosValidationException)
        {
            return (null, "422");
        }
    }

    private static bool SameOutcome((FilterNode? Filter, string? Failure) a, (FilterNode? Filter, string? Failure) b) =>
        a.Failure == b.Failure && Equals(a.Filter, b.Filter);

    // A tree with the same data whose scalars are CLR values instead of JSON-backed ones.
    private static JsonNode? Rebuild(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                var rebuiltObject = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    rebuiltObject[key] = Rebuild(value);
                }

                return rebuiltObject;
            case JsonArray array:
                var rebuiltArray = new JsonArray();
                foreach (var element in array)
                {
                    rebuiltArray.Add(Rebuild(element));
                }

                return rebuiltArray;
            default:
                var json = node.GetValue<JsonElement>();
                return json.ValueKind switch
                {
                    JsonValueKind.String => JsonValue.Create(json.GetString()),
                    JsonValueKind.True => JsonValue.Create(true),
                    JsonValueKind.False => JsonValue.Create(false),
                    JsonValueKind.Number when json.TryGetInt64(out var integer) && json.GetRawText() == integer.ToString(System.Globalization.CultureInfo.InvariantCulture) => JsonValue.Create(integer),
                    JsonValueKind.Number when json.TryGetDecimal(out var number) && json.GetRawText() == number.ToString(System.Globalization.CultureInfo.InvariantCulture) => JsonValue.Create(number),
                    JsonValueKind.Number => JsonValue.Create(json.Clone()),
                    _ => throw new InvalidOperationException($"Unexpected {json.ValueKind}."),
                };
        }
    }
    [Fact]
    public void Parse_UndefinedResourceKind_ThrowsArgumentOutOfRange() =>
        Should.Throw<ArgumentOutOfRangeException>(() => FilterParser.Parse("""{"name":"a"}""", (ResourceKind)99));

    [Fact]
    public void ExcessiveNesting_Rejected422()
    {
        var json = string.Concat(Enumerable.Repeat("""{"AND":[""", 100)) + "{}" + string.Concat(Enumerable.Repeat("]}", 100));

        Should.Throw<NachosValidationException>(() => FilterParser.Parse(json, ResourceKind.Workspace));
    }

    [Fact]
    public void InListOver1000_Rejected()
    {
        var items = string.Join(",", Enumerable.Range(0, FilterParser.MaxListItems + 1).Select(i => $"\"v{i}\""));
        var ok = string.Join(",", Enumerable.Range(0, FilterParser.MaxListItems).Select(i => $"\"v{i}\""));

        Should.Throw<NachosValidationException>(() => Parse($$$"""{"name":[{{{items}}}]}"""));
        Should.Throw<NachosValidationException>(() => Parse($$$"""{"name":{"in":[{{{items}}}]}}"""));
        Should.Throw<NachosValidationException>(() => Parse($$$$"""{"metadata":{"k":{"in":[{{{{items}}}}]}}}"""));
        Should.Throw<NachosValidationException>(() => Parse($$$"""{"metadata":{"k":[{{{items}}}]}}"""));
        Should.NotThrow(() => Parse($$$"""{"name":[{{{ok}}}]}"""));
        Should.NotThrow(() => Parse($$$$"""{"metadata":{"k":{"in":[{{{{ok}}}}]}}}"""));
        // The wildcard does not exempt an oversized list.
        Should.Throw<NachosValidationException>(() => Parse($$$"""{"name":["*",{{{items}}}]}"""));
    }

    [Theory]
    [InlineData("2026-01-01\n")]
    [InlineData("2026-01-01T00:00:00Z\n")]
    [InlineData(" 2026-01-01")]
    public void Timestamp_WithTrailingOrLeadingWhitespace_Rejected(string text)
    {
        var filter = new JsonObject { ["created_at"] = text };

        Should.Throw<NachosValidationException>(() => FilterParser.Parse(filter, ResourceKind.Workspace));
    }

    [Fact]
    public void ToString_ShowsChildren()
    {
        var node = ParseRequired("""{"OR":[{"name":"a"},{"metadata":{"a":{"b":1}}}]}""");

        var text = node.ToString();

        text.ShouldContain("Children = [");
        text.ShouldContain("Field { Column = Name");
        text.ShouldContain("Path = [a, b]");
    }

    [Fact]
    public void NeWildcard_IsLiteral()
    {
        ParseRequired("""{"name":{"ne":"*"}}""")
            .ShouldBe(new FilterNode.Field(FilterColumns.Name, FilterOp.Ne, Value("\"*\"")));
        ParseRequired("""{"metadata":{"k":{"ne":"*"}}}""")
            .ShouldBe(new FilterNode.MetadataPath(["k"], FilterOp.Ne, Value("\"*\"")));
    }

    // ------------------------------------------------------------------ AST value semantics

    [Fact]
    public void Nodes_HaveStructuralEquality()
    {
        FilterNode a = new FilterNode.And(
            [new FilterNode.Field(FilterColumns.Name, FilterOp.In, Value("""["a",1]""")), new FilterNode.MatchAll()]);
        FilterNode b = new FilterNode.And(
            [new FilterNode.Field(FilterColumns.Name, FilterOp.In, Value("""["a",1]""")), new FilterNode.MatchAll()]);
        FilterNode c = new FilterNode.And(
            [new FilterNode.Field(FilterColumns.Name, FilterOp.In, Value("""["a",2]""")), new FilterNode.MatchAll()]);

        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
        a.ShouldNotBe(c);
        ((FilterNode)new FilterNode.MatchAll()).ShouldNotBe(new FilterNode.MatchNone());
        ((FilterNode)new FilterNode.Or([])).ShouldNotBe(new FilterNode.And([]));
    }

    // ------------------------------------------------------------------ shared conformance cases

    public static IEnumerable<object[]> CaseNames => FilterCaseLibrary.CaseNameData();

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void SharedCase_ParsesOrErrorsAsExpected(string name)
    {
        var filterCase = FilterCaseLibrary.Get(name);

        if (filterCase.Error)
        {
            Should.Throw<NachosValidationException>(() => FilterParser.Parse(filterCase.Filter, filterCase.Resource));
        }
        else
        {
            Should.NotThrow(() => FilterParser.Parse(filterCase.Filter, filterCase.Resource));
        }
    }

    [Fact]
    public void SharedCases_AreComprehensive()
    {
        FilterCaseLibrary.All.Count.ShouldBeGreaterThanOrEqualTo(40);
        foreach (ResourceKind kind in Enum.GetValues<ResourceKind>())
        {
            FilterCaseLibrary.All.Count(c => c.Resource == kind).ShouldBeGreaterThan(0, kind.ToString());
        }

        FilterCaseLibrary.All.Count(c => c.Error).ShouldBeGreaterThan(5);
        FilterCaseLibrary.All.Select(c => c.Name).Distinct().Count().ShouldBe(FilterCaseLibrary.All.Count);
    }
}
