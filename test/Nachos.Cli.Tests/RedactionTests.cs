using Shouldly;

namespace Nachos.Cli.Tests;

/// <summary>
/// The tokens a connection string yields, and how messages are matched against them. Servers report a host without the protocol,
/// port or instance the operator wrote, and Azure SQL reports only the server's first DNS label (error 40615), so each piece is a
/// token on its own.
/// </summary>
public sealed class RedactionTests
{
    [Theory]
    [InlineData(@"Server=tcp:Kq7host.database.windows.net,1433", "Kq7host.database.windows.net|Kq7host")]
    [InlineData(@"Server=TCP:Kq7host.database.windows.net", "Kq7host.database.windows.net|Kq7host")]
    [InlineData(@"Data Source=admin:Kq7host.corp.example,1434", "Kq7host.corp.example|Kq7host")]
    [InlineData(@"Address=lpc:Kq7host\Inst9x", "Kq7host|Inst9x")]
    [InlineData(@"Addr=Kq7host.corp.example\Inst9x,1433", "Kq7host.corp.example|Kq7host|Inst9x")]
    [InlineData(@"Network Address=np:\\Kq7host\pipe\sql\query", "Kq7host")]
    [InlineData(@"Server='tcp:Kq7host.corp.example,1433'", "Kq7host.corp.example|Kq7host")]
    [InlineData(@"Server=127.0.0.1,1;Failover Partner=tcp:Fp4mirror.corp.example\Inst9x,1433", "Fp4mirror.corp.example|Fp4mirror|Inst9x")]
    public void Host_YieldsTheHost_ItsFirstLabel_AndTheInstance(string connectionString, string expected)
    {
        var tokens = Redaction.ConnectionStringTokens(connectionString);

        foreach (var token in expected.Split('|'))
        {
            tokens.ShouldContain(token);
        }
    }

    [Fact]
    public void IpAddress_HasNoDnsLabel()
    {
        var tokens = Redaction.ConnectionStringTokens("Server=tcp:10.20.30.40,1433");

        tokens.ShouldContain("10.20.30.40");
        tokens.ShouldNotContain("10");
    }

    [Theory]
    [InlineData("Cannot open server 'Kq7host' requested by the login. Client with IP address '203.0.113.5' is not allowed to access the server.")]
    [InlineData("Login failed on server 'kq7host.database.windows.net'.")]
    [InlineData("Instance 'inst9x' was not found on Kq7host.")]
    public void ServerMessages_NamingTheHostOrInstance_AreScrubbed(string message)
    {
        var scrubbed = Redaction.Scrub(
            message, Redaction.ConnectionStringTokens(@"Server=tcp:Kq7host.database.windows.net\Inst9x,1433;Initial Catalog=nachos"));

        scrubbed.ShouldNotContain("Kq7host", Case.Insensitive);
        scrubbed.ShouldNotContain("Inst9x", Case.Insensitive);
        scrubbed.ShouldContain(Redaction.Placeholder);
    }

    [Theory]
    [InlineData(@"AttachDbFilename=C:\Data\Hz5secret.mdf")]
    [InlineData(@"Initial File Name=|DataDirectory|\Hz5secret.mdf")]
    [InlineData(@"Extended Properties='C:\Data\Hz5secret.mdf'")]
    public void DatabaseFile_IsRedacted_ByPathAndByFileName(string attach)
    {
        var tokens = Redaction.ConnectionStringTokens($@"Server=(localdb)\MSSQLLocalDB;{attach}");

        tokens.ShouldContain("Hz5secret.mdf");
        Redaction.Scrub(@"Unable to open the physical file ""D:\App_Data\Hz5secret.mdf"".", tokens).ShouldNotContain("Hz5secret");
        Redaction.Scrub(@"An attempt to attach an auto-named database for file C:\Data\Hz5secret.mdf failed.", tokens)
            .ShouldNotContain("Hz5secret");
    }

    [Fact]
    public void ParserEchoes_AreMatchedInTheirOwnCase()
    {
        // The parser echoes a token exactly as it was typed, so a different case is a different word.
        const string message = "Required argument missing for option: '--workspace'.";

        Redaction.Echoes(message, "required").ShouldBeFalse();
        Redaction.Echoes(message, "ARGUMENT").ShouldBeFalse();
        Redaction.Echoes(message, "argument").ShouldBeTrue();
        Redaction.Echoes("Unrecognized command or argument 'Hunter2'.", "Hunter2").ShouldBeTrue();
        Redaction.Echoes("Unrecognized command or argument 'Hunter2'.", "hunter2").ShouldBeFalse();
    }

    [Fact]
    public void ConnectionStringTokens_AreScrubbedInAnyCase()
    {
        // SqlClient lowercases what it quotes.
        Redaction.Scrub("Keyword not supported: 'qw3user'.", ["Qw3User"]).ShouldBe($"Keyword not supported: '{Redaction.Placeholder}'.");
    }
}
