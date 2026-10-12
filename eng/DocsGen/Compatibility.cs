using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Nachos.DocsGen;

internal static partial class Compatibility
{
    private static readonly HashSet<string> Methods =
        new(["get", "put", "post", "delete", "options", "head", "patch", "trace"], StringComparer.Ordinal);
    private static readonly HashSet<string> Extensions = new(
        ["POST /v3/admin/grants", "GET /v3/workspaces/{workspace_id}/jobs"], StringComparer.Ordinal);

    internal static JsonObject Compare(byte[] apiBytes, byte[] manifestBytes)
    {
        using var api = JsonDocument.Parse(apiBytes);
        using var manifest = JsonDocument.Parse(manifestBytes);
        UniqueProperties(api.RootElement);
        UniqueProperties(manifest.RootElement);
        var root = api.RootElement;
        if (!OpenApiVersion().IsMatch(root.GetProperty("openapi").GetString()!) ||
            string.IsNullOrEmpty(root.GetProperty("info").GetProperty("title").GetString()) ||
            root.GetProperty("components").GetProperty("schemas").ValueKind != JsonValueKind.Object)
            throw new InvalidDataException();
        var pin = manifest.RootElement;
        if (pin.GetProperty("version").GetString() != "3.3.0" ||
            pin.GetProperty("source").GetString() != "https://docs.honcho.dev/v3/openapi.json" ||
            pin.GetProperty("sha256").GetString() != "6aa5dd7fa958719e5f12565592617621141f0b9ae0a6b5450802dc152d2e3fe9")
            throw new InvalidDataException();

        var actual = new SortedDictionary<string, (string Method, string Path, string[] Responses)>(StringComparer.Ordinal);
        foreach (var path in root.GetProperty("paths").EnumerateObject())
        {
            ValidatePath(path.Name);
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (!Methods.Contains(operation.Name))
                {
                    if (operation.Name is "summary" or "description" or "parameters" or "servers" ||
                        operation.Name.StartsWith("x-", StringComparison.Ordinal)) continue;
                    throw new InvalidDataException();
                }
                ValidateParameters(path.Name, path.Value, operation.Value);
                var responseObject = operation.Value.GetProperty("responses");
                if (responseObject.EnumerateObject().Any(response => response.Value.ValueKind != JsonValueKind.Object))
                    throw new InvalidDataException();
                var responses = responseObject.EnumerateObject()
                    .Select(response => response.Name).Order(StringComparer.Ordinal).ToArray();
                if (responses.Length == 0 || responses.Any(status => !ResponseCode().IsMatch(status)))
                    throw new InvalidDataException();
                var method = operation.Name.ToUpperInvariant();
                actual.Add(method + " " + path.Name, (method, path.Name, responses));
            }
        }
        if (actual.Count == 0) throw new InvalidDataException();

        var expected = new SortedDictionary<string, (string Method, string Path)>(StringComparer.Ordinal);
        foreach (var route in pin.GetProperty("routes").EnumerateArray())
        {
            var method = route.GetProperty("method").GetString()!;
            var path = route.GetProperty("path").GetString()!;
            ValidatePath(path);
            if (method is not ("GET" or "PUT" or "POST" or "DELETE" or "OPTIONS" or "HEAD" or "PATCH" or "TRACE"))
                throw new InvalidDataException();
            expected.Add(method + " " + path, (method, path));
        }
        if (expected.Count == 0) throw new InvalidDataException();
        var routes = new JsonArray();
        foreach (var key in actual.Keys.Union(expected.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var found = actual.TryGetValue(key, out var operation);
            var status = "missing";
            if (found)
            {
                var success = operation.Responses.Any(code => code.StartsWith('2'));
                var staged = operation.Responses.Contains("501", StringComparer.Ordinal);
                status = (success, staged) switch
                {
                    (true, false) => "declared-implemented",
                    (true, true) => "declared-partial",
                    (false, true) => "staged-501",
                    _ => "declared-other",
                };
            }
            var scope = expected.ContainsKey(key) ? "wire" : Extensions.Contains(key) ? "extension" : "unmatched";
            routes.Add(new JsonObject
            {
                ["method"] = found ? operation.Method : expected[key].Method,
                ["path"] = found ? operation.Path : expected[key].Path,
                ["scope"] = scope,
                ["status"] = status,
                ["responses"] = JsonSerializer.SerializeToNode(found ? operation.Responses : []),
            });
        }
        int Count(string field, string value) => routes.Count(row => row![field]!.GetValue<string>() == value);
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["wireVersion"] = pin.GetProperty("version").GetString(),
            ["sdkConformance"] = "not-run",
            ["authentication"] = "not-assessed",
            ["basis"] = "Exact method and path-template declarations, not runtime success. Mixed 2xx/501 is partial. Missing means absent from OpenAPI, not necessarily absent from routing.",
            ["counts"] = new JsonObject
            {
                ["actualOperations"] = actual.Count,
                ["wireOperations"] = expected.Count,
                ["declaredImplemented"] = Count("status", "declared-implemented"),
                ["declaredPartial"] = Count("status", "declared-partial"),
                ["staged501"] = Count("status", "staged-501"),
                ["declaredOther"] = Count("status", "declared-other"),
                ["missing"] = Count("status", "missing"),
                ["extensions"] = Count("scope", "extension"),
                ["unmatched"] = Count("scope", "unmatched"),
            },
            ["routes"] = routes,
        };
    }

    private static void ValidatePath(string path)
    {
        if (!PathTemplate().IsMatch(path)) throw new InvalidDataException();
        var parameters = Parameter().Matches(path).Select(match => match.Groups[1].Value).ToArray();
        if (parameters.Distinct(StringComparer.Ordinal).Count() != parameters.Length)
            throw new InvalidDataException();
    }

    private static void ValidateParameters(string path, JsonElement pathItem, JsonElement operation)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var owner in new[] { pathItem, operation })
        {
            if (!owner.TryGetProperty("parameters", out var parameters)) continue;
            foreach (var parameter in parameters.EnumerateArray())
            {
                // Explicit aliases must already be expressed by the OpenAPI producer.
                // Unresolved references cannot be guessed or normalized to a different contract.
                if (parameter.TryGetProperty("$ref", out _)) throw new InvalidDataException();
                if (parameter.GetProperty("in").GetString() != "path") continue;
                if (!parameter.GetProperty("required").GetBoolean()) throw new InvalidDataException();
                names.Add(parameter.GetProperty("name").GetString()!);
            }
        }
        if (!names.SetEquals(Parameter().Matches(path).Select(match => match.Groups[1].Value)))
            throw new InvalidDataException();
    }

    private static void UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException();
                UniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) UniqueProperties(child);
    }

    [GeneratedRegex(@"\A/(?:[A-Za-z0-9_.~-]+|\{[A-Za-z_][A-Za-z0-9_]*\})(?:/(?:[A-Za-z0-9_.~-]+|\{[A-Za-z_][A-Za-z0-9_]*\}))*\z")]
    private static partial Regex PathTemplate();
    [GeneratedRegex(@"\{([^}]+)\}")]
    private static partial Regex Parameter();
    [GeneratedRegex(@"\A(?:[1-5][0-9]{2}|[1-5]XX|default)\z")]
    private static partial Regex ResponseCode();
    [GeneratedRegex(@"\A3\.[01]\.[0-9]+\z")]
    private static partial Regex OpenApiVersion();
}
