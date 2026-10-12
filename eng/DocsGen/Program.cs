namespace Nachos.DocsGen;

internal static class Program
{
    public static Task<int> Main(string[] args) => RunAsync(args, Console.Out, Console.Error);

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            await Generator.GenerateAsync(Inputs.Parse(args));
            await output.WriteLineAsync("Generated four reference files. Declaration evidence only; SDK conformance and authentication are not assessed.");
            return 0;
        }
        catch (Exception)
        {
            // Paths, parser diagnostics and child stderr can contain credentials. Never echo them.
            await error.WriteLineAsync("DocsGen failed: invalid or missing inputs, failed CLI help, or output publication failure. See eng/DocsGen/README.md for prerequisites.");
            return 1;
        }
    }
}
