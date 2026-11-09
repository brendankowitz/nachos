namespace Nachos.Core.Tests.Hosting;

/// <summary>
/// A <see cref="TheoryAttribute"/> for tests that run a real local embedding model: it skips, with the reason, unless
/// <c>NACHOS_LOCAL_MODEL_DIR</c> names the directory <c>nachos models fetch</c> wrote. When the variable is set the test
/// runs, so a missing or wrong file fails it instead of skipping; CI sets it after fetching the models.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class RequiresLocalModelTheoryAttribute : TheoryAttribute
{
    public const string Variable = "NACHOS_LOCAL_MODEL_DIR";

    public RequiresLocalModelTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Directory))
        {
            Skip = $"Set {Variable} to the models directory written by 'nachos models fetch <model> --dir <directory>' to run the real-model tests.";
        }
    }

    public static string? Directory => Environment.GetEnvironmentVariable(Variable);
}
