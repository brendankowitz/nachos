using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Nachos.LicenseCheck;

internal static class PnpmYaml
{
    public static JsonElement[] Read(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 8 * 1024 * 1024)
            {
                throw new InvalidDataException("YAML size limit exceeded.");
            }
            var text = File.ReadAllText(path);
            var parser = new Parser(new StringReader(text));
            var count = 0;
            var depth = 0;
            while (parser.MoveNext())
            {
                if (++count > 250000 || parser.Current is AnchorAlias
                    || parser.Current is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty)
                    || parser.Current is Scalar scalar && scalar.Value.Length > 65536)
                {
                    throw new InvalidDataException("Unsupported YAML alias, anchor, tag or size limit.");
                }
                if (parser.Current is MappingStart or SequenceStart && ++depth > 64)
                {
                    throw new InvalidDataException("YAML depth limit exceeded.");
                }
                if (parser.Current is MappingEnd or SequenceEnd) depth--;
            }
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            return stream.Documents.Select(document => JsonSerializer.SerializeToElement(Convert(document.RootNode))).ToArray();
        }
        catch (Exception exception) when (exception is YamlException or ArgumentException or InvalidDataException)
        {
            throw new InvalidDataException($"pnpm YAML {path}: {exception.Message}", exception);
        }
    }

    private static JsonNode Convert(YamlNode node) => node switch
    {
        YamlScalarNode scalar => JsonValue.Create(scalar.Value ?? "")!,
        YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(Convert).ToArray()),
        YamlMappingNode mapping => Object(mapping),
        _ => throw new InvalidDataException("Unsupported YAML node.")
    };

    private static JsonObject Object(YamlMappingNode mapping)
    {
        var result = new JsonObject();
        foreach (var (key, value) in mapping.Children)
        {
            if (key is not YamlScalarNode { Value: { Length: > 0 } name } || name == "<<" || result.ContainsKey(name))
            {
                throw new InvalidDataException("Duplicate, merged or non-scalar YAML mapping key.");
            }
            result.Add(name, Convert(value));
        }
        return result;
    }
}
