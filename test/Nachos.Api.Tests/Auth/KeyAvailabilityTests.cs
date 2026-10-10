using Shouldly;

namespace Nachos.Api.Tests.Auth;

public sealed class KeyAvailabilityTests
{
    public static TheoryData<string> Queries => new()
    {
        "", "workspace_id=A", "peer_id=p1", "session_id=s1",
        "workspace_id=A&peer_id=p1&session_id=s1", "workspace_id=",
        "workspace_id=A&expires_at=not-a-date",
    };

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task KeylessEntraAdmin_AlwaysGetsExactAvailabilityErrorBeforeQueryValidation(string query)
    {
        using var entra = new OfflineEntra();
        using var host = new AuthHost { Entra = entra, SigningKeys = new() };
        var error = await host.Send("POST", "/v3/keys?" + query, null, entra.Token("Nachos.Admin"), 422);
        error.GetProperty("detail").GetString().ShouldBe("key issuance requires configured signing keys");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Nachos.Workspace")]
    public async Task KeylessAvailability_DoesNotPrecedeAuthenticationOrAdminAuthorization(string? role)
    {
        using var entra = new OfflineEntra();
        using var host = new AuthHost { Entra = entra, SigningKeys = new() };
        await host.Send("POST", "/v3/keys?expires_at=not-a-date", null,
            role is null ? null : entra.Token(role), 401);
    }

    [Theory]
    [InlineData("")]
    [InlineData("peer_id=p1")]
    [InlineData("workspace_id=A&expires_at=not-a-date")]
    public async Task ConfiguredRing_RetainsSpecificQueryAndScopeValidation(string query)
    {
        using var host = new AuthHost();
        var error = await host.Send("POST", "/v3/keys?" + query, null, AuthHost.Key("admin"), 422);
        error.GetRawText().ShouldNotContain("key issuance requires configured signing keys");
        if (query.Contains("expires_at", StringComparison.Ordinal))
            error.GetProperty("detail")[0].GetProperty("type").GetString().ShouldBe("datetime_parsing");
    }
}
