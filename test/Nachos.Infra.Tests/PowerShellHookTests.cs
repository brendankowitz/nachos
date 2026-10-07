using System.Net;
using Shouldly;

namespace Nachos.Infra.Tests;

/// <summary>
/// Behavioural tests of the Windows hooks: the real <c>.ps1</c> files run under <c>pwsh</c> against the same fake
/// <c>az</c>/<c>sqlcmd</c>/<c>dotnet</c> as the bash tests, and postdeploy.ps1 polls a 127.0.0.1 listener.
/// POSIX-gated like the other fake-toolbox tests (the fakes are bash scripts).
/// </summary>
public sealed class PowerShellHookTests
{
    private const string Key = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhZG1pbiJ9.c2lnbmF0dXJl";

    [RequiresPosixToolFact("pwsh")]
    public void PostdeployPs1_AcceptsHealthy()
    {
        foreach (var body in new[] { "Healthy", "Healthy\r\n" })
        {
            using var toolbox = new FakeToolbox();
            using var api = new ScriptedHttpServer((HttpStatusCode.OK, body));

            var result = toolbox.RunPostdeployPs1(api.Uri);

            result.ExitCode.ShouldBe(0, $"body '{body}': {result.StdErr}");
            api.Requests.ShouldBe(1);
        }
    }

    [RequiresPosixToolFact("pwsh")]
    public void PostdeployPs1_KeepsPolling_WhileTrafficStillReachesThePlaceholder()
    {
        using var toolbox = new FakeToolbox();
        using var api = new ScriptedHttpServer(
            (HttpStatusCode.OK, InfraTests.EchoBody), (HttpStatusCode.ServiceUnavailable, "Unhealthy"), (HttpStatusCode.OK, "Healthy"));

        var result = toolbox.RunPostdeployPs1(api.Uri);

        result.ExitCode.ShouldBe(0, result.StdErr);
        api.Requests.ShouldBe(3);
        toolbox.Count("sleep 6").ShouldBe(2);
    }

    [RequiresPosixToolFact("pwsh")]
    public void PostdeployPs1_FailsAfterTheWholeBudget_WhenTheBodyIsNeverExactlyHealthy()
    {
        foreach (var (status, body) in new[] { (HttpStatusCode.OK, InfraTests.EchoBody), (HttpStatusCode.OK, "Degraded"), (HttpStatusCode.OK, "{\"status\":\"Healthy\"}") })
        {
            using var toolbox = new FakeToolbox();
            using var api = new ScriptedHttpServer((status, body));

            var result = toolbox.RunPostdeployPs1(api.Uri);

            result.ExitCode.ShouldNotBe(0, $"body '{body}' must be rejected");
            (result.StdOut + result.StdErr).ShouldContain(InfraTests.PlaceholderStillServing);
            api.Requests.ShouldBe(InfraTests.PostdeployAttempts, $"body '{body}' is retried for the whole budget");
            toolbox.Count("sleep 6").ShouldBe(InfraTests.PostdeployAttempts - 1);
        }
    }

    [RequiresPosixToolFact("pwsh")]
    public void PostprovisionPs1_AbortsOnAStaleSid_BeforeAnyKeyVaultCall()
    {
        using var toolbox = new FakeToolbox();

        var result = toolbox.RunPostprovisionPs1(existingSid: "0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF", listNames: "nachos-signing-key-0", keyOutput: Key + "\n");

        result.ExitCode.ShouldNotBe(0);
        var output = result.StdOut + result.StdErr;
        output.ShouldContain("id-test");
        output.ShouldContain("recreated");
        toolbox.Count("-i ").ShouldBe(0, "no user/role script may run against a stale user");
        toolbox.Count("keyvault").ShouldBe(0, "nothing may touch Key Vault");
        toolbox.Count("firewall-rule delete").ShouldBe(1, "the temporary firewall rule is always removed");
    }

    [RequiresPosixToolFact("pwsh")]
    public void PostprovisionPs1_WithTheExpectedSid_MintsWithKidZero()
    {
        using var toolbox = new FakeToolbox();

        var result = toolbox.RunPostprovisionPs1(existingSid: FakeToolbox.ExpectedSid, listNames: "nachos-signing-key-0", keyOutput: Key + "\n");

        result.ExitCode.ShouldBe(0, result.StdErr);
        toolbox.Calls.Where(c => c.Contains("keys create", StringComparison.Ordinal)).ShouldHaveSingleItem()
            .ShouldContain("--admin --kid 0 --signing-secret-env NACHOS_SIGNING_SECRET");
        toolbox.StoredSecret("nachos-bootstrap-admin-key").ShouldBe(Key);
        (result.StdOut + result.StdErr).ShouldNotContain(Key);
        toolbox.Count("firewall-rule delete").ShouldBe(1);
    }

    [RequiresPosixToolFact("pwsh")]
    public void PostprovisionPs1_WithNoExistingUser_CreatesItAndContinues()
    {
        using var toolbox = new FakeToolbox();

        var result = toolbox.RunPostprovisionPs1(listNames: "nachos-bootstrap-admin-key");

        result.ExitCode.ShouldBe(0, result.StdErr);
        toolbox.Count("-i ").ShouldBe(1, "the user/role script runs");
        toolbox.Count("keyvault secret list").ShouldBe(1);
    }
}
