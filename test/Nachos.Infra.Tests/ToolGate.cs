namespace Nachos.Infra.Tests;

public enum GateOutcome
{
    /// <summary>Run the test.</summary>
    Run,

    /// <summary>Skip with an explicit reason.</summary>
    Skip,

    /// <summary>Run the test and let it fail loudly because a required tool is missing.</summary>
    Fail,
}

internal sealed record GateDecision(GateOutcome Outcome, string? Reason = null);

/// <summary>
/// Decides whether a tool-dependent test runs, skips or fails. Kept pure so the policy is unit-tested without
/// needing a Windows machine or a missing tool.
/// </summary>
internal static class ToolGate
{
    public const string PosixOnlyReason = "Needs a POSIX /usr/bin:/bin environment; covered in Linux CI.";

    /// <param name="posixOnly">
    /// The test fakes tools through a POSIX PATH. On Windows it is skipped even when
    /// <paramref name="requireTools"/> is set: that variable is only set in Linux CI, and a Windows host
    /// (bash only via Git-for-Windows/WSL shims) must not fail a teammate's local run.
    /// </param>
    public static GateDecision Decide(bool isWindows, bool requireTools, bool toolPresent, bool posixOnly, string toolDescription)
    {
        if (posixOnly && isWindows)
        {
            return new GateDecision(GateOutcome.Skip, PosixOnlyReason);
        }

        if (toolPresent)
        {
            return new GateDecision(GateOutcome.Run);
        }

        return requireTools
            ? new GateDecision(GateOutcome.Fail)
            : new GateDecision(
                GateOutcome.Skip,
                $"Requires {toolDescription} on PATH (set NACHOS_REQUIRE_INFRA_TOOLS=1 to make this a failure).");
    }

    /// <summary>
    /// Like <see cref="Decide"/> for a test that needs a RUNNING Docker daemon (the CLI alone is not enough): a missing
    /// CLI or a stopped daemon skips, or fails under <paramref name="requireTools"/>. POSIX-only.
    /// </summary>
    public static GateDecision DecideDocker(bool isWindows, bool requireTools, bool cliPresent, bool daemonRunning)
    {
        var decision = Decide(isWindows, requireTools, cliPresent && daemonRunning, posixOnly: true, "docker");
        return decision.Outcome == GateOutcome.Skip && decision.Reason != PosixOnlyReason
            ? decision with
            {
                Reason = "Requires a running Docker daemon (the docker CLI on PATH and a successful `docker info`); "
                    + "set NACHOS_REQUIRE_INFRA_TOOLS=1 to make this a failure.",
            }
            : decision;
    }
}
