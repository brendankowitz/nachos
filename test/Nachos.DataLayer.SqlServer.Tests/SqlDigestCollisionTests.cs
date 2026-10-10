using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.DataLayer.SqlServer.Filtering;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// Forced-collision proof: long-operand <c>in</c> membership uses a SHA-256 + kind + length prefilter, confirmed by exact
/// comparison, so a digest collision cannot produce a match. The compiler's test seam shortens the prefilter digest (to
/// no hex digits at all, or two) so that different operands and stored values of one length share a prefilter entry; the
/// results must still be exactly those of the full digest, and those of exact equality.
/// </summary>
[Collection(SqlServerDockerGroup.Name)]
public sealed class SqlDigestCollisionTests(SqlServerFixture fixture)
{
    private const string Workspace = "collisions";
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static bool _seeded;

    /// <summary>Same-length long strings (20 units), differing in one code unit, in case, or in a surrogate.</summary>
    private static readonly string S1 = new string('s', 19) + "1";
    private static readonly string S2 = new string('s', 19) + "2";
    private static readonly string S3 = new string('s', 19) + "3";
    private static readonly string S4 = new string('S', 19) + "1";
    private static readonly string S5 = new string('s', 18) + "😀";

    /// <summary>Same-length long numbers (order keys of the same length over 100 characters).</summary>
    private static readonly string N1 = "1." + new string('3', 95) + "1";
    private static readonly string N2 = "1." + new string('3', 95) + "2";
    private static readonly string N3 = "1." + new string('3', 95) + "3";
    private static readonly string N4 = "-1." + new string('3', 95) + "1";

    /// <summary>A 21-unit and a 99-digit operand that nothing stored equals; each only shares a length with stored values.</summary>
    private static readonly string T21 = new string('t', 21);
    private static readonly string N5 = "1." + new string('3', 96) + "1";

    /// <summary>A 20-unit string and a number of N1's key length that nothing stored equals.</summary>
    private static readonly string S6 = new string('s', 19) + "6";
    private static readonly string N6 = "1." + new string('3', 95) + "6";

    /// <remarks>
    /// Every list has at least two operands: the parser turns a one-element <c>in</c> into an equality, which never uses
    /// the prefilter. Under the seam, entries hold only lengths, so operands of different lengths take the packed path
    /// (each operand collides with every stored value of its length, and the position of the hit selects the one operand
    /// to confirm), and operands of one length form a collision group confirmed against all its members.
    /// </remarks>
    public static TheoryData<string, string[]> Cases()
    {
        static string Q(string s) => JsonValue.Create(s)!.ToJsonString();
        static string In(params string[] items) => "{\"metadata\":{\"k\":{\"in\":[" + string.Join(",", items) + "]}}}";
        return new TheoryData<string, string[]>
        {
            // Packed path: S1 shares its entry with s2, s4 and s5; T21 with s3x.
            { In(Q(S1), Q(T21)), ["s1"] },
            { In(Q(S2), Q(S3 + "y")), ["s2"] },
            { In(Q(S4), Q(S3 + "x")), ["s4", "s3x"] },
            { In(Q(S5), Q(T21)), ["s5"] },
            { In(Q(S3), Q(T21)), [] },
            { In(N1, N5), ["n1"] },
            { In(N3, N5), [] },
            { In(N4, N2), ["n4", "n2"] },

            // Collision buckets (addendum 3): three operands share one entry; a stored value equal to the second or third
            // matches, and a stored non-member of the same length (s1, s4 / n1) does not. A lookup that confirmed only the
            // first operand of a bucket would miss them.
            { In(Q(S3), Q(S2), Q(S5)), ["s2", "s5"] },
            { In(Q(S3), Q(S6), Q(S4)), ["s4"] },
            { In(Q(S6), Q(S5), Q(S3)), ["s5"] },
            { In(N3, N1, N2), ["n1", "n2"] },
            { In(N3, N6, N2), ["n2"] },
            { In(N6, N2, N3), ["n2"] },

            // Collision groups: several operands of one length share one entry.
            { In(Q(S1), Q(S3)), ["s1"] },
            { In(Q(S3), Q(new string('s', 19) + "4")), [] },
            { In(N1, N3), ["n1"] },
            { In(N2, N3, N4), ["n2", "n4"] },

            // Mixed kinds and tiers: a string never matches a number row, and short operands stay exact.
            { In(Q(S1), N1, Q("x"), "1"), ["s1", "n1", "x", "one"] },
            { In(Q(N1), N3), [] },
            { "{\"NOT\":[" + In(Q(S1), N2) + "]}", ["s2", "s3x", "s4", "s5", "n1", "n4", "x", "one"] },
            { "{\"NOT\":[" + In(Q(S3), Q(T21), N3, N5) + "]}", ["s1", "s2", "s3x", "s4", "s5", "n1", "n2", "n4", "x", "one"] },
        };
    }
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task DigestCollisions_NeverMatch_ExactValuesDo(string filterJson, string[] expected)
    {
        var database = await SeedAsync();
        var filter = FilterParser.Parse(filterJson, ResourceKind.Peer)!;

        foreach (var hexLength in new[] { 0, 2, SqlDigest.FullHexLength })
        {
            (await NamesAsync(database, filter, hexLength)).ShouldBe(expected, ignoreOrder: true, $"digest of {hexLength} hex digits: {filterJson}");
        }

        // The store (full digest) agrees.
        var page = await database.CreateStore(TimeProvider.System).Peers.ListAsync(Workspace, PeerKind.All, filter, new PageRequest(1, 100), Ct);
        page.Items.Select(p => p.Name).ShouldBe(expected, ignoreOrder: true);
    }

    [Fact]
    public void Seam_ForcesThreeOperandBuckets()
    {
        // The buckets the cases above rely on really form: at 0 hex digits the three operands of each share one entry,
        // as do the stored non-members of their length.
        new[] { S3, S2, S5, S6, S4, S1 }.Select(s => SqlDigest.OfString(s, 0)).Distinct().ShouldHaveSingleItem();
        new[] { N3, N1, N2, N6 }.Select(n => SqlDigest.OfKey(Key(n), 0)).Distinct().ShouldHaveSingleItem();
        new[] { S3, S2, S5 }.Select(s => SqlDigest.OfString(s)).Distinct().Count().ShouldBe(3);

        static string Key(string number) => Storage.ExactDecimal.Parse(number).ToOrderKey();
    }

    [Fact]
    public void Seam_ForcesSharedPrefilterEntries()
    {
        // Sanity of the seam itself: with no hex digits, different same-length operands share an entry; the full digest
        // keeps them apart.
        SqlDigest.OfString(S1, 0).ShouldBe(SqlDigest.OfString(S2, 0));
        SqlDigest.OfKey(Key(N1), 0).ShouldBe(SqlDigest.OfKey(Key(N2), 0));
        SqlDigest.OfString(S1).ShouldNotBe(SqlDigest.OfString(S2));
        SqlDigest.OfString(S1, 0).ShouldNotBe(SqlDigest.OfString(S1 + "x", 0));

        static string Key(string number) => Storage.ExactDecimal.Parse(number).ToOrderKey();
    }

    private async Task<SqlTestDatabase> SeedAsync()
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "digest-collisions");
        await SeedLock.WaitAsync(Ct);
        try
        {
            if (!_seeded)
            {
                var store = database.CreateStore(TimeProvider.System);
                await store.Workspaces.GetOrCreateAsync(Workspace, null, null, Ct);
                (string Name, JsonNode Value)[] peers =
                [
                    ("s1", S1), ("s2", S2), ("s3x", S3 + "x"), ("s4", S4), ("s5", S5), ("x", "x"),
                    ("n1", JsonNode.Parse(N1)!), ("n2", JsonNode.Parse(N2)!), ("n4", JsonNode.Parse(N4)!), ("one", 1),
                ];
                foreach (var (name, value) in peers)
                {
                    await store.Peers.GetOrCreateAsync(Workspace, name, new JsonObject { ["k"] = value }, null, Ct);
                }

                _seeded = true;
            }
        }
        finally
        {
            SeedLock.Release();
        }

        return database;
    }

    private static async Task<List<string>> NamesAsync(SqlTestDatabase database, FilterNode filter, int hexLength)
    {
        var (where, parameters) = SqlFilterCompiler.Compile(filter, ResourceKind.Peer, "t", hexLength);
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand($"SELECT t.Name FROM dbo.Peers AS t WHERE {where}", connection);
        command.Parameters.AddRange([.. parameters]);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
