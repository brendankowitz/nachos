using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Nachos.Core.Keys;
using Nachos.Testing;
using Shouldly;

namespace Nachos.Api.Tests.Auth;

public sealed class CredentialEndpointTests
{
    [Theory]
    [InlineData("expired")]
    [InlineData("rotated-out")]
    [InlineData("unknown-kid")]
    [InlineData("tampered")]
    [InlineData("malformed")]
    [InlineData("issuer")]
    [InlineData("null-issuer")]
    public async Task InvalidNachosCredential_NeverBecomesTrusted(string invalid)
    {
        var ring = invalid is "rotated-out" or "unknown-kid"
            ? new SigningKeyOptions { Keys = [new("retired", AuthHost.Secret)] } : AuthHost.Ring;
        var issuer = new HmacKeyIssuer(Options.Create(ring), TimeProvider.System);
        var token = issuer.Issue(new(true, null, null, null, invalid == "expired" ? DateTimeOffset.UtcNow.AddMinutes(-1) : null));
        if (invalid == "malformed") token = "not.a.jwt";
        if (invalid == "tampered")
        {
            var pieces = token.Split('.');
            pieces[1] = Base64UrlEncoder.Encode("""{"ad":true,"w":"wrong"}""");
            token = string.Join('.', pieces);
        }
        if (invalid is "issuer" or "null-issuer")
            token = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
            {
                Claims = new Dictionary<string, object> { ["ad"] = true, ["iss"] = invalid == "issuer" ? "synthetic" : null! },
                SigningCredentials = new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(AuthHost.Secret)) { KeyId = "current" },
                    SecurityAlgorithms.HmacSha256),
            });
        using var host = new AuthHost();
        await host.Send("POST", "/v3/workspaces", """{"id":"A"}""", token, 401);
    }

    [Fact]
    public async Task Rotation_ValidatesOldKeyButSignsWithFirst()
    {
        using var host = new AuthHost
        {
            SigningKeys = new() { Keys = [new("next", AuthHost.Secret + "-next"), .. AuthHost.Ring.Keys] },
        };
        var result = await host.Send("POST", "/v3/keys?workspace_id=A", null, AuthHost.Key("admin"), 200);
        var token = result.GetProperty("key").GetString()!;
        new JsonWebToken(token).Kid.ShouldBe("next");
        await host.Send("POST", "/v3/workspaces", """{"id":"A"}""", token, 200);
    }

    [Theory]
    [InlineData("")]
    [InlineData("peer_id=p1")]
    [InlineData("session_id=s1")]
    [InlineData("workspace_id=A&peer_id=p1&session_id=s1")]
    [InlineData("workspace_id=")]
    [InlineData("workspace_id=A&expires_at=not-a-date")]
    public async Task InvalidKeyScope_Returns422WithoutAdminIssuance(string query)
    {
        using var host = new AuthHost();
        await host.Send("POST", "/v3/keys?" + query, null, AuthHost.Key("admin"), 422);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"object_id":"x"}""")]
    [InlineData("""{"role":"Nachos.Workspace"}""")]
    [InlineData("""{"object_id":"","role":"Nachos.Workspace"}""")]
    [InlineData("""{"object_id":"x","role":"other"}""")]
    public async Task InvalidGrant_Returns422(string body)
    {
        using var host = new AuthHost();
        await host.Send("POST", "/v3/admin/grants", body, AuthHost.Key("admin"), 422);
        (await host.Store.Grants.ListAsync("x", default)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_IsAnonymous(string path)
    {
        using var host = new AuthHost();
        using var response = await host.Http.GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WithAuth_UsesExplicitRingAndDisabledFixtureCannotIssue()
    {
        using var original = new NachosApiFactory();
        using var auth = original.WithAuth(AuthHost.Ring);
        using var http = auth.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v3/workspaces")
        {
            Content = new StringContent("""{"id":"A"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new("Bearer", AuthHost.Key("admin"));
        using var response = await http.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var disabled = original.CreateClient();
        using var denied = await disabled.PostAsync("/v3/keys", new StringContent("""{"workspace_id":"A"}""", Encoding.UTF8, "application/json"));
        denied.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await denied.Content.ReadAsStringAsync()).ShouldContain("key issuance requires configured signing keys");
    }
}
