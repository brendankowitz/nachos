using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Nachos.Abstractions;
using Nachos.Core.Configuration;
using Nachos.Core.Tokens;
using NSubstitute;
using Shouldly;

namespace Nachos.Core.Tests;

public sealed class NachosOptionsTests
{
    [Theory]
    [InlineData("short", 9)]
    [InlineData("long", 19)]
    [InlineData("short_tokens", 0)]
    [InlineData("long_tokens", -1)]
    [InlineData("custom_tokens", 0)]
    public void Resolver_RejectsInvalidDeploymentLimits(string field, int value)
    {
        var options = new NachosOptions();
        switch (field)
        {
            case "short": options.Summary.MessagesPerShort = value; break;
            case "long": options.Summary.MessagesPerLong = value; break;
            case "short_tokens": options.Summary.MaxTokensShort = value; break;
            case "long_tokens": options.Summary.MaxTokensLong = value; break;
            case "custom_tokens": options.Deriver.MaxCustomInstructionsTokens = value; break;
        }

        Should.Throw<OptionsValidationException>(() =>
            new ConfigurationResolver(Options.Create(options), Substitute.For<ITokenCounter>()));
    }

    [Theory]
    [InlineData("CustomInstructions")]
    [InlineData("Reasoning.CustomInstructions")]
    [InlineData("PeerCard.CustomInstructions")]
    [InlineData("Summary.CustomInstructions")]
    [InlineData("Dream.CustomInstructions")]
    [InlineData("Dialectic.CustomInstructions")]
    public void Resolver_RejectsOversizedDeploymentInstructionsAsOptionErrors(string property)
    {
        var options = WithInstructions(property, "oversized");
        var counter = Substitute.For<ITokenCounter>();
        counter.Count("oversized").Returns(2001);

        var error = Should.Throw<OptionsValidationException>(() =>
            new ConfigurationResolver(Options.Create(options), counter));

        error.OptionsType.ShouldBe(typeof(NachosOptions));
        error.OptionsName.ShouldBe(Options.DefaultName);
        error.Failures.ShouldHaveSingleItem().ShouldContain(property);
    }

    [Theory]
    [InlineData("CustomInstructions")]
    [InlineData("Reasoning.CustomInstructions")]
    [InlineData("PeerCard.CustomInstructions")]
    [InlineData("Summary.CustomInstructions")]
    [InlineData("Dream.CustomInstructions")]
    [InlineData("Dialectic.CustomInstructions")]
    public void Resolver_AcceptsDeploymentInstructionsExactlyAtTheBudget(string property)
    {
        var options = WithInstructions(property, "at limit");
        var counter = Substitute.For<ITokenCounter>();
        counter.Count("at limit").Returns(2000);

        Should.NotThrow(() => new ConfigurationResolver(Options.Create(options), counter).Resolve(null));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CounterFailures_RetainIdentityForDeploymentAndResources(bool deployment, bool domainFailure)
    {
        var options = deployment ? WithInstructions("Summary.CustomInstructions", "counter fails") : new NachosOptions();
        Exception failure = domainFailure
            ? new NachosValidationException("counter failed independently of the budget")
            : new InvalidOperationException("counter unavailable");
        var counter = Substitute.For<ITokenCounter>();
        counter.Count("counter fails").Returns(_ => throw failure);

        var actual = Should.Throw<Exception>(() =>
        {
            var resolver = new ConfigurationResolver(Options.Create(options), counter);
            if (!deployment)
            {
                resolver.Resolve(new JsonObject
                {
                    ["summary"] = new JsonObject { ["custom_instructions"] = "counter fails" },
                });
            }
        });

        actual.ShouldBeSameAs(failure);
    }

    [Theory]
    [InlineData("Summary")]
    [InlineData("Deriver")]
    public void Resolver_RejectsMissingRequiredDeploymentSections(string section)
    {
        var options = new NachosOptions();
        if (section == "Summary")
        {
            options.Summary = null!;
        }
        else
        {
            options.Deriver = null!;
        }

        var error = Should.Throw<OptionsValidationException>(() =>
            new ConfigurationResolver(Options.Create(options), Substitute.For<ITokenCounter>()));

        error.Failures.ShouldContain(failure => failure.Contains(section, StringComparison.Ordinal));
    }

    [Fact]
    public void Resolver_AcceptsDeploymentLimitsAtTheirMinimums()
    {
        var options = new NachosOptions
        {
            Summary = new() { MessagesPerShort = 10, MessagesPerLong = 20, MaxTokensShort = 1, MaxTokensLong = 1 },
            Deriver = new() { MaxCustomInstructionsTokens = 1 },
        };

        var result = new ConfigurationResolver(Options.Create(options), Substitute.For<ITokenCounter>()).Resolve(null);

        result.Summary.MessagesPerShortSummary.Value.ShouldBe(10);
        result.Summary.MessagesPerLongSummary.Value.ShouldBe(20);
        result.Summary.MaxTokensShort.Value.ShouldBe(1);
        result.Summary.MaxTokensLong.Value.ShouldBe(1);
        result.MaxCustomInstructionsTokens.Value.ShouldBe(1);
    }

    private static NachosOptions WithInstructions(string property, string value)
    {
        var options = new NachosOptions();
        switch (property)
        {
            case "CustomInstructions": options.CustomInstructions = value; break;
            case "Reasoning.CustomInstructions": options.Reasoning = new(CustomInstructions: value); break;
            case "PeerCard.CustomInstructions": options.PeerCard = new(CustomInstructions: value); break;
            case "Summary.CustomInstructions": options.Summary.CustomInstructions = value; break;
            case "Dream.CustomInstructions": options.Dream = new(CustomInstructions: value); break;
            case "Dialectic.CustomInstructions": options.Dialectic = new(value); break;
            default: throw new ArgumentOutOfRangeException(nameof(property));
        }

        return options;
    }
}
