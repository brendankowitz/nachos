namespace Nachos.Client;

/// <summary>Connection settings for <see cref="NachosHttpClient"/>.</summary>
/// <remarks>
/// A class rather than a record on purpose: a record's generated <c>ToString</c> would print <see cref="ApiKey"/>.
/// </remarks>
public sealed class NachosClientOptions
{
    /// <summary>Absolute root of the Nachos server; <c>v3/...</c> routes are resolved beneath it.</summary>
    public required Uri BaseAddress { get; set; }

    /// <summary>A NachosKey token, sent as <c>Authorization: Bearer &lt;key&gt;</c>. Null sends no credentials.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Entra scopes requested for the token credential.</summary>
    public string[] Scopes { get; set; } = [];

    // TODO(task-12-credential): add `TokenCredential? Credential` (Entra) once Azure.Core is pinned in
    // Directory.Packages.props; NachosHttpClient's authorization path then prefers it over ApiKey.
}
