using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Nachos.Abstractions;
using Nachos.Core.Keys;

namespace Nachos.Api.Auth;

internal sealed class NachosKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IKeyIssuer issuer, IConfiguration configuration) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!configuration.GetValue("Nachos:Auth:Enabled", true)) return Task.FromResult(AuthenticateResult.NoResult());
        var token = BearerCredential.Read(Request);
        if (token is null) return Task.FromResult(AuthenticateResult.NoResult());
        if (BearerCredential.HasIssuer(token)) return Task.FromResult(AuthenticateResult.Fail("Invalid credential."));
        try
        {
            var claims = issuer.Validate(token);
            var principal = new NachosPrincipal(Scheme.Name, claims.Admin && claims.Workspace is null,
                claims.Workspace is null ? [] : [claims.Workspace], claims.Peer, claims.Session);
            return Task.FromResult(AuthenticateResult.Success(new(principal, Scheme.Name)));
        }
        catch (AuthException)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid credential."));
        }
    }
}
