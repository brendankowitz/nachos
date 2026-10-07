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
        if (!Tools.ToolsAreRequired && Tools.Find(anyOfTools) is null)
        {
            Skip = $"Requires one of [{string.Join(", ", anyOfTools)}] on PATH (set NACHOS_REQUIRE_INFRA_TOOLS=1 to make this a failure).";
        }
    }
}
