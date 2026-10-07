using Shouldly;

namespace Nachos.Infra.Tests;

public sealed class ToolGateTests
{
    [Theory]
    // isWindows, requireTools, toolPresent, posixOnly, expected
    [InlineData(false, false, true, false, GateOutcome.Run)]
    [InlineData(false, false, false, false, GateOutcome.Skip)]
    [InlineData(false, true, false, false, GateOutcome.Fail)]
    [InlineData(false, true, true, false, GateOutcome.Run)]
    [InlineData(true, false, true, false, GateOutcome.Run)]
    [InlineData(true, false, false, false, GateOutcome.Skip)]
    [InlineData(true, true, false, false, GateOutcome.Fail)]
    [InlineData(true, true, true, false, GateOutcome.Run)]
    // POSIX-only (fake toolbox) tests
    [InlineData(false, false, true, true, GateOutcome.Run)]
    [InlineData(false, false, false, true, GateOutcome.Skip)]
    [InlineData(false, true, false, true, GateOutcome.Fail)]
    [InlineData(false, true, true, true, GateOutcome.Run)]
    [InlineData(true, false, true, true, GateOutcome.Skip)]
    [InlineData(true, false, false, true, GateOutcome.Skip)]
    [InlineData(true, true, true, true, GateOutcome.Skip)]
    [InlineData(true, true, false, true, GateOutcome.Skip)]
    public void Decide_FollowsThePolicy(bool isWindows, bool requireTools, bool toolPresent, bool posixOnly, GateOutcome expected)
    {
        var decision = ToolGate.Decide(isWindows, requireTools, toolPresent, posixOnly, "bash");

        decision.Outcome.ShouldBe(expected);
        (decision.Reason is null).ShouldBe(expected != GateOutcome.Skip);
    }

    [Fact]
    public void Decide_ExplainsWhyAPosixOnlyTestWasSkippedOnWindows()
    {
        ToolGate.Decide(isWindows: true, requireTools: true, toolPresent: true, posixOnly: true, "bash")
            .Reason.ShouldBe(ToolGate.PosixOnlyReason);
    }
}
