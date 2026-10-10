namespace Nachos.Infra.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips, with an explicit reason, when none of the named tools
/// is installed. When <c>NACHOS_REQUIRE_INFRA_TOOLS=1</c> the test is NOT skipped; it runs and fails
/// loudly (see <see cref="Tools.Require"/>), so CI cannot pass by silently skipping.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class RequiresToolFactAttribute : FactAttribute
{
    public RequiresToolFactAttribute(params string[] anyOfTools)
    {
        var decision = ToolGate.Decide(
            OperatingSystem.IsWindows(),
            Tools.ToolsAreRequired,
            Tools.Find(anyOfTools) is not null,
            posixOnly: false,
            $"one of [{string.Join(", ", anyOfTools)}]");
        if (decision.Outcome == GateOutcome.Skip)
        {
            Skip = decision.Reason;
        }
    }
}

/// <summary>
/// Like <see cref="RequiresToolFactAttribute"/>, for tests that fake tools through a POSIX PATH
/// (<c>FakeToolbox</c>). They always skip on Windows, even under <c>NACHOS_REQUIRE_INFRA_TOOLS=1</c>, because
/// that variable is only set in Linux CI.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class RequiresPosixToolFactAttribute : FactAttribute
{
    public RequiresPosixToolFactAttribute(params string[] anyOfTools)
    {
        var decision = ToolGate.Decide(
            OperatingSystem.IsWindows(),
            Tools.ToolsAreRequired,
            Tools.Find(anyOfTools) is not null,
            posixOnly: true,
            $"one of [{string.Join(", ", anyOfTools)}]");
        if (decision.Outcome == GateOutcome.Skip)
        {
            Skip = decision.Reason;
        }
    }
}

/// <summary>
/// For tests that run containers: skips (with a reason) when the docker CLI is missing or its daemon is not running,
/// unless <c>NACHOS_REQUIRE_INFRA_TOOLS=1</c>, which makes either a failure. A daemon that runs but cannot pull is
/// left to the test itself, which then fails.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class RequiresDockerDaemonFactAttribute : FactAttribute
{
    public RequiresDockerDaemonFactAttribute()
    {
        var docker = Tools.Find("docker");
        var decision = ToolGate.DecideDocker(
            OperatingSystem.IsWindows(),
            Tools.ToolsAreRequired,
            docker is not null,
            docker is not null && DaemonAnswers(docker));
        if (decision.Outcome == GateOutcome.Skip)
        {
            Skip = decision.Reason;
        }
    }

    private static bool DaemonAnswers(string docker)
    {
        try
        {
            return Tools.Run(docker, ["info", "--format", "{{.ServerVersion}}"], timeout: TimeSpan.FromSeconds(15)).ExitCode == 0;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
