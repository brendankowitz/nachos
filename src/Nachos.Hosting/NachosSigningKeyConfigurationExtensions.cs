using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Nachos.Core.Keys;
using Nachos.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

public static class NachosSigningKeyConfigurationExtensions
{
    /// <summary>
    /// Binds a signing-key section (normally Nachos:Auth:NachosKey) when options are resolved.
    /// Malformed shapes fail with value-free configuration errors; entries are never discarded.
    /// A Keys section without children binds an empty ring when its value is null or empty.
    /// In the JSON provider, [] aliases "" and null aliases {} at this flattened boundary.
    /// Upper-provider null/{} values do not clear lower-provider key children; []/"" with lower
    /// children is a rejected scalar/list hybrid. A standalone explicit empty binding replaces
    /// any prior programmatic ring with empty options, so the issuer cannot issue.
    /// Provider order is retained without imposing an index-name policy. This does not enable
    /// reload or eager startup validation.
    /// </summary>
    public static NachosBuilder BindSigningKeys(this NachosBuilder builder, IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(section);
        builder.Services.Configure<SigningKeyOptions>(options => options.Keys = ReadKeys(section));
        return builder;
    }

    private static List<SigningKey> ReadKeys(IConfigurationSection section)
    {
        if (section.Value is not null ||
            section.GetChildren().Any(child => !child.Key.Equals("Keys", StringComparison.OrdinalIgnoreCase)))
        {
            throw Invalid("Signing key configuration must contain only a Keys section.");
        }

        var keys = section.GetSection("Keys");
        if (!string.IsNullOrEmpty(keys.Value) || (keys.Value is not null && keys.GetChildren().Any()))
            throw Invalid("Keys must be an ordered key list.");

        var ring = new List<SigningKey>();
        foreach (var entry in keys.GetChildren())
        {
            var kid = entry.GetSection("Kid").Value;
            var secret = entry.GetSection("Secret").Value;
            if (entry.Value is not null || kid is null || secret is null ||
                entry.GetChildren().Any(field => field.GetChildren().Any() ||
                    (!field.Key.Equals("Kid", StringComparison.OrdinalIgnoreCase) &&
                     !field.Key.Equals("Secret", StringComparison.OrdinalIgnoreCase))))
            {
                throw Invalid($"Keys[{ring.Count}] requires an object with scalar Kid and Secret fields.");
            }
            ring.Add(new SigningKey(kid, secret));
        }
        return ring;
    }

    private static OptionsValidationException Invalid(string failure) =>
        new(Options.Options.DefaultName, typeof(SigningKeyOptions), [failure]);
}
