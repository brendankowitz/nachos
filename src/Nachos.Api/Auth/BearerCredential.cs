using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Nachos.Api.Auth;

internal static class BearerCredential
{
    internal static string? Read(HttpRequest request) =>
        request.Headers.Authorization.Count == 1 &&
        AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out var header) &&
        header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            ? header.Parameter : null;

    internal static bool HasIssuer(string? token)
    {
        if (token is null) return false;
        var parts = token.Split('.');
        if (parts.Length != 3) return false;
        try
        {
            using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[1]));
            return payload.RootElement.ValueKind == JsonValueKind.Object && payload.RootElement.TryGetProperty("iss", out _);
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException)
        {
            return false;
        }
    }
}
