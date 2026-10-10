using Microsoft.Extensions.Options;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Stores;
using Nachos.Core.Configuration;
using Nachos.Core.Keys;
using Nachos.Core.Tokens;
using Nachos.Core.Validation;
using NSubstitute;
using Shouldly;

namespace Nachos.Core.Tests;

public sealed class ConfigurationAdmissionTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void SummaryMinimums_ReportTheExactRequestField(bool session, bool shortSummary)
    {
        var validator = CreateValidator();
        var summary = shortSummary ? new SummaryConfiguration(MessagesPerShortSummary: 9) :
            new SummaryConfiguration(MessagesPerLongSummary: 19);

        var error = Should.Throw<RequestValidationException>(() =>
        {
            if (session) validator.ValidateSessionConfiguration(new(Summary: summary));
            else validator.ValidateWorkspaceConfiguration(new(Summary: summary));
        });

        var failure = error.Errors.ShouldHaveSingleItem();
        failure.Loc.ShouldBe(new object[] { "body", "configuration", "summary",
            shortSummary ? "messages_per_short_summary" : "messages_per_long_summary" });
        failure.Type.ShouldBe("greater_than_equal");
    }

    [Fact]
    public void BothInvalidMinimums_ReportBothFields()
    {
        var error = Should.Throw<RequestValidationException>(() => CreateValidator()
            .ValidateWorkspaceConfiguration(new(Summary: new(MessagesPerShortSummary: 9, MessagesPerLongSummary: 19))));

        error.Errors.Select(failure => failure.Loc[^1])
            .ShouldBe(new object[] { "messages_per_short_summary", "messages_per_long_summary" });
    }

    [Theory]
    [InlineData("workspace create")]
    [InlineData("workspace update")]
    [InlineData("session create")]
    [InlineData("session update")]
    public async Task AllAdmissionSurfaces_RejectSummaryBeforeStoreAccess(string operation)
    {
        var store = Substitute.For<IMemoryStore>();
        var counter = new LengthCounter();
        var service = new NachosService(store, counter, CreateValidator(), Substitute.For<IKeyIssuer>());
        var error = await Should.ThrowAsync<RequestValidationException>(() => operation switch
        {
            "workspace create" => service.GetOrCreateWorkspaceAsync("W",
                configuration: new(Summary: new(MessagesPerShortSummary: 9))),
            "workspace update" => service.UpdateWorkspaceAsync("W",
                configuration: new(Summary: new(MessagesPerShortSummary: 9))),
            "session create" => service.GetOrCreateSessionAsync("W", "S",
                configuration: new(Summary: new(MessagesPerShortSummary: 9))),
            "session update" => service.UpdateSessionAsync("W", "S",
                configuration: new(Summary: new(MessagesPerShortSummary: 9))),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        });

        error.Errors.ShouldHaveSingleItem().Loc.ShouldBe(
            new object[] { "body", "configuration", "summary", "messages_per_short_summary" });
        store.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("reasoning")]
    [InlineData("peer_card")]
    [InlineData("summary")]
    [InlineData("dream")]
    [InlineData("dialectic")]
    [InlineData("")]
    public void ResourceInstructionBudgets_RemainDomainErrorsAtAdmission(string section)
    {
        var validator = CreateValidator();
        var configuration = section switch
        {
            "reasoning" => new WorkspaceConfiguration(Reasoning: new(CustomInstructions: "long")),
            "peer_card" => new WorkspaceConfiguration(PeerCard: new(CustomInstructions: "long")),
            "summary" => new WorkspaceConfiguration(Summary: new(CustomInstructions: "long")),
            "dream" => new WorkspaceConfiguration(Dream: new(CustomInstructions: "long")),
            "dialectic" => new WorkspaceConfiguration(Dialectic: new("long")),
            "" => new WorkspaceConfiguration(CustomInstructions: "long"),
            _ => throw new ArgumentOutOfRangeException(nameof(section)),
        };

        Should.Throw<NachosValidationException>(() => validator.ValidateWorkspaceConfiguration(configuration));
        Should.Throw<NachosValidationException>(() => validator.ValidateSessionConfiguration(new(
            configuration.Reasoning, configuration.PeerCard, configuration.Summary,
            configuration.Dream, configuration.Dialectic, configuration.CustomInstructions)));
        if (section == "reasoning")
            Should.Throw<NachosValidationException>(() => validator.ValidateMessageConfiguration(new(configuration.Reasoning)));
    }

    [Fact]
    public void ValidMinimumsNullInheritanceAndIds_KeepTheirExistingContracts()
    {
        var validator = CreateValidator();
        validator.ValidateWorkspaceConfiguration(new(Summary: new(MessagesPerShortSummary: 10, MessagesPerLongSummary: 20)));
        validator.ValidateSessionConfiguration(new(Summary: new()));
        validator.ValidateWorkspaceConfiguration(null);
        Should.Throw<NachosValidationException>(() => IdValidator.Validate("bad id", "id"));
    }

    private static RequestValidator CreateValidator() =>
        new(Options.Create(new NachosOptions { Deriver = new() { MaxCustomInstructionsTokens = 3 } }), new LengthCounter());

    private sealed class LengthCounter : ITokenCounter
    {
        public int Count(string text) => text.Length;
    }
}
