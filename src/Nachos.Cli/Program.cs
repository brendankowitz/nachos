using System.Globalization;
using System.Text;
using Nachos.Cli;

// Fixed so that errors from the parser, SqlClient and DacFx are not localized: the hooks and operators read them.
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

// A redirected stream is read by a program or saved to a file (the report XML declares encoding="utf-8"), so it is written as UTF-8
// without a byte order mark instead of in the console's code page. Console.OutputEncoding is left alone: setting it changes the code
// page of the console this process shares with its parent. An interactive console keeps Console.Out, which writes Unicode to it.
var output = Console.IsOutputRedirected ? Utf8Writer(Console.OpenStandardOutput()) : Console.Out;
var error = Console.IsErrorRedirected ? Utf8Writer(Console.OpenStandardError()) : Console.Error;

return await CliApp.RunAsync(args, output, error, CancellationToken.None);

static TextWriter Utf8Writer(Stream stream) => new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
