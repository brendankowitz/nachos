using System.Text.Json.Nodes;
using Nachos.Abstractions.Filtering;
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
        field.Value!.GetValue<long>().ShouldBe(5);
    }

    [Theory]
    [InlineData("""{"token_count":"abc"}""")]
    [InlineData("""{"token_count":"5.5"}""")]
    [InlineData("""{"token_count":5.5}""")]
    [InlineData("""{"token_count":" 5"}""")]
    [InlineData("""{"token_count":true}""")]
    [InlineData("""{"token_count":{"gt":"9223372036854775808"}}""")]
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
    [InlineData("""{"metadata":null}""")]
    [InlineData("""{"metadata":["a"]}""")]
    public void MetadataNotAnObject_Rejected(string json) =>
        Should.Throw<NachosValidationException>(() => Parse(json));

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
