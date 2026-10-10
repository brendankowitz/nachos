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

    [Theory]
    // isWindows, requireTools, cliPresent, daemonRunning, expected
    [InlineData(false, false, true, true, GateOutcome.Run)]
    [InlineData(false, false, true, false, GateOutcome.Skip)]
    [InlineData(false, false, false, false, GateOutcome.Skip)]
    [InlineData(false, true, true, false, GateOutcome.Fail)]
    [InlineData(false, true, false, false, GateOutcome.Fail)]
    [InlineData(false, true, true, true, GateOutcome.Run)]
    [InlineData(true, true, true, true, GateOutcome.Skip)]
    public void DecideDocker_NeedsARunningDaemon(bool isWindows, bool requireTools, bool cliPresent, bool daemonRunning, GateOutcome expected)
    {
        var decision = ToolGate.DecideDocker(isWindows, requireTools, cliPresent, daemonRunning);

        decision.Outcome.ShouldBe(expected);
        if (expected == GateOutcome.Skip && !isWindows)
        {
            decision.Reason.ShouldNotBeNull().ShouldContain("Docker daemon");
        }
    }

    [Fact]
    public void Decide_ExplainsWhyAPosixOnlyTestWasSkippedOnWindows()
    {
        ToolGate.Decide(isWindows: true, requireTools: true, toolPresent: true, posixOnly: true, "bash")
            .Reason.ShouldBe(ToolGate.PosixOnlyReason);
    }
}
