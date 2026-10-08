using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.Options;
using Nachos.Core.Keys;

namespace Nachos.Cli;

/// <summary><c>nachos keys create</c>: mints a key offline with <see cref="HmacKeyIssuer"/>. Only the token reaches standard output.</summary>
internal static class KeyCommands
{
    public static Command Create()
    {
        var admin = new Option<bool>("--admin") { Description = "Mint an admin key." };
        var workspace = new Option<string>("--workspace") { Description = "Scope the key to this workspace." };
        var peer = new Option<string>("--peer") { Description = "Narrow a workspace key to this peer (not with --session)." };
        var session = new Option<string>("--session") { Description = "Narrow a workspace key to this session (not with --peer)." };
        var expires = new Option<string>("--expires") { Description = "ISO-8601 expiry; must be in the future. A value without an offset is UTC." };
        var secret = new Option<string>("--signing-secret") { Description = "The signing secret (at least 32 UTF-8 bytes). Prefer --signing-secret-env." };
        var secretEnv = new Option<string>("--signing-secret-env") { Description = "Name of the environment variable holding the signing secret." };
        var kid = new Option<string>("--kid") { Description = "Key id stamped in the token header.", DefaultValueFactory = _ => "0" };

        var create = new Command("create", "Mint a key. Writes only the token to standard output.")
        {
            admin, workspace, peer, session, expires, secret, secretEnv, kid,
        };
        create.SetAction((parse, _) => CommandFailure.GuardAsync(parse.InvocationConfiguration.Error, async () =>
        {
            var io = parse.InvocationConfiguration;
            var scopeError = ScopeError(parse.GetValue(admin), parse.GetValue(workspace), parse.GetValue(peer), parse.GetValue(session));
            if (scopeError is not null)
            {
                await io.Error.WriteLineAsync($"error: {scopeError}");
                return ExitCodes.Error;
            }

            var (signingSecret, secretError) = ResolveSecret(parse.GetValue(secret), parse.GetValue(secretEnv));
            if (signingSecret is null)
            {
                await io.Error.WriteLineAsync($"error: {secretError}");
                return ExitCodes.Error;
            }

            var (expiresAt, expiresError) = ParseExpires(parse.GetValue(expires), TimeProvider.System.GetUtcNow());
            if (expiresError is not null)
            {
                await io.Error.WriteLineAsync($"error: {expiresError}");
                return ExitCodes.Error;
            }

            var keys = new SigningKeyOptions { Keys = [new SigningKey(parse.GetValue(kid)!, signingSecret)] };
            var issuer = new HmacKeyIssuer(Options.Create(keys), TimeProvider.System);
            var token = issuer.Issue(new NachosKeyClaims(
                parse.GetValue(admin), parse.GetValue(workspace), parse.GetValue(peer), parse.GetValue(session), expiresAt));

            // Hooks capture standard output as the token, so write exactly one line feed regardless of platform.
            await io.Output.WriteAsync(token);
            await io.Output.WriteAsync('\n');
            return ExitCodes.Success;
        }));

        return new Command("keys", "Mint and inspect keys offline.") { create };
    }

    private static string? ScopeError(bool admin, string? workspace, string? peer, string? session)
    {
        if (admin)
        {
            return workspace is null && peer is null && session is null
                ? null
                : "--admin cannot be combined with --workspace, --peer or --session.";
        }

        if (workspace is null)
        {
            return peer is null && session is null
                ? "A key needs a scope: pass --admin or --workspace."
                : "--peer and --session need --workspace.";
        }

        return peer is not null && session is not null ? "--peer and --session cannot be combined." : null;
    }

    // Messages never include the secret itself.
    private static (string? Secret, string? Error) ResolveSecret(string? literal, string? variable)
    {
        if (literal is not null && variable is not null)
        {
            return (null, "--signing-secret and --signing-secret-env cannot be combined.");
        }

        if (literal is null && variable is null)
        {
            return (null, "A signing secret is required: pass --signing-secret-env <VAR> (or --signing-secret).");
        }

        if (literal is not null)
        {
            return literal.Length == 0 ? (null, "--signing-secret is empty.") : (literal, null);
        }

        var value = Environment.GetEnvironmentVariable(variable!);
        return string.IsNullOrEmpty(value) ? (null, $"The environment variable {variable} is missing or empty.") : (value, null);
    }

    private static (DateTimeOffset? Value, string? Error) ParseExpires(string? text, DateTimeOffset now)
    {
        if (text is null)
        {
            return (null, null);
        }

        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.RoundtripKind, out var value))
        {
            return (null, "--expires must be an ISO-8601 date and time, for example 2030-01-31T12:00:00Z.");
        }

        return value <= now ? (null, "--expires must be in the future.") : (value, null);
    }
}
