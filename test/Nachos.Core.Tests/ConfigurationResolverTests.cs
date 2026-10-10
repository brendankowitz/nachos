using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Core.Configuration;
using Nachos.Core.Tokens;
using Nachos.Core.Validation;
using NSubstitute;
using Shouldly;

namespace Nachos.Core.Tests;

public sealed class ConfigurationResolverTests
{
    private static ConfigurationResolver CreateResolver(NachosOptions? options = null, ITokenCounter? counter = null) =>
        new(Options.Create(options ?? new NachosOptions()), counter ?? Substitute.For<ITokenCounter>());

    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void AbsentConfiguration_UsesDocumentedDeploymentDefaultsWithProvenance()
    {
        var resolver = new ConfigurationResolver(Options.Create(new NachosOptions()), Substitute.For<ITokenCounter>());

        var result = resolver.Resolve(null, null, null);

        result.Summary.MessagesPerShortSummary.ShouldBe(new ResolvedValue<int>(20, "global"));
        result.Summary.MessagesPerLongSummary.ShouldBe(new ResolvedValue<int>(60, "global"));
        result.Summary.MaxTokensShort.ShouldBe(new ResolvedValue<int>(1000, "global"));
        result.Summary.MaxTokensLong.ShouldBe(new ResolvedValue<int>(4000, "global"));
        result.MaxCustomInstructionsTokens.ShouldBe(new ResolvedValue<int>(2000, "global"));
        result.Reasoning.Enabled.ShouldBe(new ResolvedValue<bool?>(null, "global"));
        result.CustomInstructions.ShouldBe(new ResolvedValue<string?>(null, "global"));
    }

    [Fact]
    public void MessageOverridesSessionOverridesWorkspace_PerLeaf()
    {
        var workspace = Json("""{"reasoning":{"enabled":true,"custom_instructions":"workspace"},"summary":{"messages_per_short_summary":30,"messages_per_long_summary":80},"custom_instructions":"workspace root"}""");
        var session = Json("""{"reasoning":{"enabled":false,"custom_instructions":"session"},"summary":{"messages_per_long_summary":90}}""");
        var message = Json("""{"reasoning":{"custom_instructions":"message"}}""");

        var result = CreateResolver().Resolve(workspace, session, message);

        result.Reasoning.Enabled.ShouldBe(new ResolvedValue<bool?>(false, "session"));
        result.Reasoning.CustomInstructions.ShouldBe(new ResolvedValue<string?>("message", "message"));
        result.Summary.MessagesPerShortSummary.ShouldBe(new ResolvedValue<int>(30, "workspace"));
        result.Summary.MessagesPerLongSummary.ShouldBe(new ResolvedValue<int>(90, "session"));
        result.CustomInstructions.ShouldBe(new ResolvedValue<string?>("workspace root", "workspace"));
        result.Summary.MaxTokensShort.Source.ShouldBe("global");
    }

    [Fact]
    public void MessageConfiguration_OnlyAffectsReasoning()
    {
        var session = Json("""{"summary":{"messages_per_short_summary":31},"custom_instructions":"session"}""");
        var message = Json("""{"reasoning":{"enabled":false},"summary":{"messages_per_short_summary":9},"custom_instructions":123,"peer_card":"not a config","dream":{"enabled":true},"dialectic":{"custom_instructions":"ignored"}}""");

        var result = CreateResolver().Resolve(null, session, message);

        result.Reasoning.Enabled.ShouldBe(new ResolvedValue<bool?>(false, "message"));
        result.Summary.MessagesPerShortSummary.ShouldBe(new ResolvedValue<int>(31, "session"));
        result.CustomInstructions.ShouldBe(new ResolvedValue<string?>("session", "session"));
        result.PeerCard.Use.ShouldBe(new ResolvedValue<bool?>(null, "global"));
        result.Dream.Enabled.ShouldBe(new ResolvedValue<bool?>(null, "global"));
        result.Dialectic.CustomInstructions.ShouldBe(new ResolvedValue<string?>(null, "global"));
    }

    [Fact]
    public void NullInherits_WhileFalseAndEmptyStringOverride()
    {
        var options = new NachosOptions { Reasoning = new(true, "global") };
        var workspace = Json("""{"reasoning":{"enabled":false,"custom_instructions":""}}""");
        var session = Json("""{"reasoning":{"enabled":null,"custom_instructions":null}}""");

        var result = CreateResolver(options).Resolve(workspace, session, Json("""{"reasoning":null}"""));

        result.Reasoning.Enabled.ShouldBe(new ResolvedValue<bool?>(false, "workspace"));
        result.Reasoning.CustomInstructions.ShouldBe(new ResolvedValue<string?>("", "workspace"));
    }

    [Fact]
    public void AllResourceFields_ResolveWithTheirOwnProvenance()
    {
        var workspace = Json("""{"reasoning":{"enabled":true},"peer_card":{"use":true,"create":false,"custom_instructions":"card"},"summary":{"enabled":false,"custom_instructions":"summary"},"dream":{"enabled":true,"custom_instructions":"dream"},"dialectic":{"custom_instructions":"dialectic"},"custom_instructions":"root"}""");
        var session = Json("""{"peer_card":{"create":true},"dream":{"custom_instructions":"session dream"}}""");

        var result = CreateResolver().Resolve(workspace, session);

        result.Reasoning.Enabled.ShouldBe(new ResolvedValue<bool?>(true, "workspace"));
        result.PeerCard.Use.ShouldBe(new ResolvedValue<bool?>(true, "workspace"));
        result.PeerCard.Create.ShouldBe(new ResolvedValue<bool?>(true, "session"));
        result.PeerCard.CustomInstructions.ShouldBe(new ResolvedValue<string?>("card", "workspace"));
        result.Summary.Enabled.ShouldBe(new ResolvedValue<bool?>(false, "workspace"));
        result.Summary.CustomInstructions.ShouldBe(new ResolvedValue<string?>("summary", "workspace"));
        result.Dream.Enabled.ShouldBe(new ResolvedValue<bool?>(true, "workspace"));
        result.Dream.CustomInstructions.ShouldBe(new ResolvedValue<string?>("session dream", "session"));
        result.Dialectic.CustomInstructions.ShouldBe(new ResolvedValue<string?>("dialectic", "workspace"));
        result.CustomInstructions.ShouldBe(new ResolvedValue<string?>("root", "workspace"));
    }

    [Fact]
    public void DeploymentSettings_AreUsedInsteadOfHardcodedDefaults()
    {
        var options = new NachosOptions
        {
            Reasoning = new(false, "global reasoning"),
            PeerCard = new(true, false, "global card"),
            Summary = new() { Enabled = true, MessagesPerShort = 11, MessagesPerLong = 22,
                MaxTokensShort = 333, MaxTokensLong = 444, CustomInstructions = "global summary" },
            Dream = new(false, "global dream"),
            Dialectic = new("global dialectic"),
            CustomInstructions = "global root",
            Deriver = new() { MaxCustomInstructionsTokens = 555 },
        };

        var result = CreateResolver(options).Resolve(null);

        result.Summary.MessagesPerShortSummary.ShouldBe(new ResolvedValue<int>(11, "global"));
        result.Summary.MessagesPerLongSummary.ShouldBe(new ResolvedValue<int>(22, "global"));
        result.Summary.MaxTokensShort.ShouldBe(new ResolvedValue<int>(333, "global"));
        result.Summary.MaxTokensLong.ShouldBe(new ResolvedValue<int>(444, "global"));
        result.MaxCustomInstructionsTokens.ShouldBe(new ResolvedValue<int>(555, "global"));
        result.Reasoning.CustomInstructions.ShouldBe(new ResolvedValue<string?>("global reasoning", "global"));
        result.PeerCard.CustomInstructions.ShouldBe(new ResolvedValue<string?>("global card", "global"));
        result.Summary.CustomInstructions.ShouldBe(new ResolvedValue<string?>("global summary", "global"));
        result.Dream.CustomInstructions.ShouldBe(new ResolvedValue<string?>("global dream", "global"));
        result.Dialectic.CustomInstructions.ShouldBe(new ResolvedValue<string?>("global dialectic", "global"));
        result.CustomInstructions.ShouldBe(new ResolvedValue<string?>("global root", "global"));
    }

    [Theory]
    [InlineData("messages_per_short_summary", 9)]
    [InlineData("messages_per_long_summary", 19)]
    public void SummaryMinimums_AreEnforcedEvenInShadowedLayers(string field, int value)
    {
        var invalid = new JsonObject { ["summary"] = new JsonObject { [field] = value } };
        var valid = new JsonObject { ["summary"] = new JsonObject { [field] = 100 } };

        Should.Throw<InvalidOperationException>(() => CreateResolver().Resolve(invalid, valid))
            .Message.ShouldContain("workspace");
        Should.Throw<InvalidOperationException>(() => CreateResolver().Resolve(null, invalid))
            .Message.ShouldContain("session");
    }

    [Fact]
    public void SummaryMinimums_AreInclusive()
    {
        var result = CreateResolver().Resolve(Json("""{"summary":{"messages_per_short_summary":10,"messages_per_long_summary":20}}"""));

        result.Summary.MessagesPerShortSummary.Value.ShouldBe(10);
        result.Summary.MessagesPerLongSummary.Value.ShouldBe(20);
    }

    [Theory]
    [InlineData("""{"reasoning":true}""")]
    [InlineData("""{"reasoning":{"enabled":"false"}}""")]
    [InlineData("""{"summary":{"messages_per_short_summary":10.5}}""")]
    [InlineData("""{"custom_instructions":42}""")]
    [InlineData("""{"peer_card":{"use":[]}}""")]
    public void MalformedKnownFields_ProduceNonRequestConfigurationErrors(string json)
    {
        var error = Should.Throw<InvalidOperationException>(() => CreateResolver().Resolve(Json(json)));

        error.Message.ShouldContain("workspace");
        error.InnerException.ShouldBeOfType<System.Text.Json.JsonException>();
        Should.Throw<InvalidOperationException>(() => CreateResolver().Resolve(null, Json(json)))
            .Message.ShouldContain("session");
    }

    [Theory]
    [InlineData("reasoning")]
    [InlineData("peer_card")]
    [InlineData("summary")]
    [InlineData("dream")]
    [InlineData("dialectic")]
    [InlineData("")]
    public void StoredInstructionFields_AreUnchangedAndNotRecountedAfterBudgetLowering(string section)
    {
        var counter = new CountingCounter();
        var value = new JsonObject { ["custom_instructions"] = "too many tokens" };
        var config = section.Length == 0 ? value : new JsonObject { [section] = value };
        var admitted = System.Text.Json.JsonSerializer.Deserialize<Nachos.Abstractions.Contracts.WorkspaceConfiguration>(config);
        new RequestValidator(Options.Create(new NachosOptions()), counter).ValidateWorkspaceConfiguration(admitted);
        counter.Calls.ShouldBe(1);
        var options = new NachosOptions { Deriver = new() { MaxCustomInstructionsTokens = 3 } };
        var resolver = CreateResolver(options, counter);

        foreach (var layer in new[] { "workspace", "session" })
        {
            var result = layer == "workspace" ? resolver.Resolve(config) : resolver.Resolve(null, config);
            var instructions = section switch
            {
                "reasoning" => result.Reasoning.CustomInstructions,
                "peer_card" => result.PeerCard.CustomInstructions,
                "summary" => result.Summary.CustomInstructions,
                "dream" => result.Dream.CustomInstructions,
                "dialectic" => result.Dialectic.CustomInstructions,
                _ => result.CustomInstructions,
            };
            instructions.ShouldBe(new ResolvedValue<string?>("too many tokens", layer));
            result.MaxCustomInstructionsTokens.Value.ShouldBe(3);
        }
        counter.Calls.ShouldBe(1);
    }

    [Fact]
    public void StoredMessageInstructions_AreNotRecountedAfterBudgetLowering()
    {
        var counter = new CountingCounter();
        new RequestValidator(Options.Create(new NachosOptions()), counter)
            .ValidateMessageConfiguration(new(new(CustomInstructions: "oversized")));
        counter.Calls.ShouldBe(1);
        var resolver = CreateResolver(new NachosOptions { Deriver = new() { MaxCustomInstructionsTokens = 3 } }, counter);

        resolver.Resolve(null, null, Json("""{"reasoning":{"custom_instructions":"oversized"}}"""))
            .Reasoning.CustomInstructions.ShouldBe(new ResolvedValue<string?>("oversized", "message"));
        counter.Calls.ShouldBe(1);
    }

    [Fact]
    public void MalformedMessageReasoning_IsANonRequestConfigurationError()
    {
        Should.Throw<InvalidOperationException>(() =>
            CreateResolver().Resolve(null, null, Json("""{"reasoning":{"enabled":"false"}}""")))
            .Message.ShouldContain("message");
    }

    [Fact]
    public void InstructionsAtTheLimit_AreKeptUnmodified()
    {
        var counter = Substitute.For<ITokenCounter>();
        counter.Count("keep me").Returns(2000);

        var result = CreateResolver(counter: counter).Resolve(Json("""{"custom_instructions":"keep me"}"""));

        result.CustomInstructions.ShouldBe(new ResolvedValue<string?>("keep me", "workspace"));
    }

    [Fact]
    public void UnknownProperties_DoNotBecomeResolvedWireFields()
    {
        var result = CreateResolver().Resolve(Json("""{"unknown":42,"summary":{"max_tokens_short":5,"messages_per_short_summary":12}}"""));

        result.Summary.MessagesPerShortSummary.ShouldBe(new ResolvedValue<int>(12, "workspace"));
        result.Summary.MaxTokensShort.ShouldBe(new ResolvedValue<int>(1000, "global"));
    }

    [Fact]
    public void Resolve_DoesNotMutateOrRetainCallerJson()
    {
        var workspace = Json("""{"reasoning":{"enabled":true},"summary":{"messages_per_short_summary":12}}""");
        var original = workspace.ToJsonString();

        var result = CreateResolver().Resolve(workspace);
        workspace.ToJsonString().ShouldBe(original);
        workspace["reasoning"]!["enabled"] = false;
        workspace["summary"]!["messages_per_short_summary"] = 90;

        result.Reasoning.Enabled.Value.ShouldBe(true);
        result.Summary.MessagesPerShortSummary.Value.ShouldBe(12);
    }

    private sealed class CountingCounter : ITokenCounter
    {
        public int Calls { get; private set; }
        public int Count(string text)
        {
            Calls++;
            return text.Length;
        }
    }
}
