using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Nachos.Abstractions;
using Nachos.Core.Validation;

namespace Nachos.Core.Keys;

public sealed class HmacKeyIssuer : IKeyIssuer
{
    private readonly SymmetricSecurityKey[] _keys;
    private readonly TimeProvider _clock;
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    public HmacKeyIssuer(IOptions<SigningKeyOptions> options, TimeProvider clock)
    {
        var configured = options.Value.Keys;
        if (configured is null)
        {
            throw InvalidConfiguration("Keys must be an ordered key list.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        _keys = new SymmetricSecurityKey[configured.Count];
        for (var index = 0; index < configured.Count; index++)
        {
            var key = configured[index];
            if (key is null || string.IsNullOrWhiteSpace(key.Kid) || !ids.Add(key.Kid) ||
                string.IsNullOrWhiteSpace(key.Secret) || Encoding.UTF8.GetByteCount(key.Secret) < 32)
            {
                throw InvalidConfiguration(
                    $"Keys[{index}] requires a unique nonempty Kid and a UTF-8 Secret of at least 32 bytes.");
            }

            _keys[index] = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key.Secret)) { KeyId = key.Kid };
        }

        _clock = clock;
    }

    public string Issue(NachosKeyClaims claims)
    {
        if (_keys.Length == 0)
        {
            throw new NachosValidationException("key issuance requires configured signing keys");
        }

        ValidateScope(claims);
        var payload = new Dictionary<string, object>
        {
            ["t"] = _clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
        };
        if (claims.Admin)
        {
            payload["ad"] = true;
        }

        if (claims.Workspace is not null)
        {
            payload["w"] = claims.Workspace;
        }

        if (claims.Peer is not null)
        {
            payload["p"] = claims.Peer;
        }

        if (claims.Session is not null)
        {
            payload["s"] = claims.Session;
        }

        if (claims.ExpiresAt is { } expiry)
        {
            payload["exp"] = (expiry - DateTimeOffset.UnixEpoch).Ticks / (decimal)TimeSpan.TicksPerSecond;
        }

        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Claims = payload,
            SigningCredentials = new SigningCredentials(_keys[0], SecurityAlgorithms.HmacSha256)
        });
    }

    public NachosKeyClaims Validate(string token)
    {
        if (_keys.Length == 0 || string.IsNullOrEmpty(token) || !_handler.CanReadToken(token))
        {
            throw InvalidToken();
        }

        var now = (_clock.GetUtcNow() - DateTimeOffset.UnixEpoch).Ticks;
        try
        {
            var jwt = _handler.ReadJsonWebToken(token);
            using var header = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(jwt.EncodedHeader));
            using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(jwt.EncodedPayload));
            RequireUniqueObject(header.RootElement);
            RequireUniqueObject(payload.RootElement);
            var kid = ReadString(header.RootElement, "kid");
            var key = kid is null ? _keys[0] : Array.Find(_keys,
                candidate => string.Equals(candidate.KeyId, kid, StringComparison.Ordinal));
            if (key is null)
            {
                throw InvalidToken();
            }

            var expires = ReadNumericDate(payload.RootElement, "exp");
            var notBefore = ReadNumericDate(payload.RootElement, "nbf");
            var parameters = new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                IssuerSigningKeyResolver = (_, _, _, _) => [key],
                TryAllIssuerSigningKeys = false,
                RequireExpirationTime = false,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero,
                // Validate original NumericDates before either the library or DTO can round them.
                LifetimeValidator = (_, _, _, _) =>
                    (expires is null || expires.CompareToUnixTicks(now) > 0) &&
                    (notBefore is null || notBefore.CompareToUnixTicks(now) <= 0),
                IncludeTokenOnFailedValidation = false,
                LogTokenId = false
            };
            // The published boundary is synchronous. Validation uses local keys only, with no remote
            // configuration or async resolvers; bridge the supported API rather than obsolete ValidateToken.
            var result = _handler.ValidateTokenAsync(token, parameters).GetAwaiter().GetResult();
            if (!result.IsValid)
            {
                throw InvalidToken();
            }

            return ReadClaims(payload.RootElement, expires);
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException
            or SecurityTokenException or NachosValidationException)
        {
            // Library diagnostics may contain the original token. Never attach them to the domain error.
            throw InvalidToken();
        }
    }

    private static NachosKeyClaims ReadClaims(JsonElement payload, NumericDate? expires)
    {
        if (!payload.TryGetProperty("t", out var time) || time.ValueKind != JsonValueKind.String ||
            !time.TryGetDateTimeOffset(out var timestamp) || timestamp.Offset != TimeSpan.Zero)
        {
            throw InvalidToken();
        }

        var text = time.GetString()!;
        if (!text.EndsWith('Z') && !text.EndsWith("+00:00", StringComparison.Ordinal) &&
            !text.EndsWith("-00:00", StringComparison.Ordinal))
        {
            throw InvalidToken();
        }

        var admin = false;
        if (payload.TryGetProperty("ad", out var flag))
        {
            if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw InvalidToken();
            }

            admin = flag.GetBoolean();
        }

        var claims = new NachosKeyClaims(admin, ReadString(payload, "w"), ReadString(payload, "p"),
            ReadString(payload, "s"), expires?.ToDateTimeOffset());
        ValidateScope(claims);
        return claims;
    }

    private static void ValidateScope(NachosKeyClaims claims)
    {
        if ((claims.Workspace is null && (!claims.Admin || claims.Peer is not null || claims.Session is not null)) ||
            (claims.Peer is not null && claims.Session is not null))
        {
            throw new NachosValidationException(
                "Key claims require explicit admin or workspace scope; peer/session scopes require a workspace and are mutually exclusive.");
        }

        if (claims.Workspace is { } workspace)
        {
            IdValidator.Validate(workspace, "w");
        }

        if (claims.Peer is { } peer)
        {
            IdValidator.Validate(peer, "p");
        }

        if (claims.Session is { } session)
        {
            IdValidator.Validate(session, "s");
        }
    }

    private static string? ReadString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw InvalidToken();
        }

        return property.GetString();
    }

    private static NumericDate? ReadNumericDate(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Number)
        {
            throw InvalidToken();
        }

        return NumericDate.Parse(property);
    }

    private static void RequireUniqueObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw InvalidToken();
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw InvalidToken();
            }
        }
    }

    private static AuthException InvalidToken() => new("Invalid Nachos key.");

    private static OptionsValidationException InvalidConfiguration(string failure) =>
        new(Options.DefaultName, typeof(SigningKeyOptions), [failure]);
}
