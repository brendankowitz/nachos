using System.Globalization;
using Nachos.Cli;

// Fixed so that errors from the parser, SqlClient and DacFx are not localized: the hooks and operators read them.
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

return await CliApp.RunAsync(args, Console.Out, Console.Error, CancellationToken.None);
