using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Nachos.Abstractions.Contracts;
using Nachos.Core.Configuration;
using Nachos.DataLayer.SqlServer.Schema;

namespace Nachos.DocsGen;

internal static class OptionsReference
{
    internal static string Render(Inputs inputs)
    {
        var assemblies = new[]
        {
            (typeof(NachosOptions).Assembly, "src/Nachos.Core"),
            (typeof(SqlServerOptions).Assembly, "src/DataLayer/Nachos.DataLayer.SqlServer"),
            (typeof(ReasoningConfiguration).Assembly, "src/Nachos.Abstractions"),
        };
        var docs = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var (assembly, project) in assemblies)
        {
            var name = assembly.GetName().Name!;
            var stem = $"{project}/bin/{inputs.Configuration}/net10.0/{name}";
            var binary = inputs.Read(stem + ".dll");
            if (!binary.AsSpan().SequenceEqual(File.ReadAllBytes(assembly.Location))) throw new InvalidDataException();
            using var stream = new MemoryStream(inputs.Read($"eng/DocsGen/obj/options-xml/{inputs.Configuration}/{name}.xml"));
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 8 * 1024 * 1024,
            });
            var xml = XDocument.Load(reader);
            if (xml.Root?.Element("assembly")?.Element("name")?.Value != name) throw new InvalidDataException();
            foreach (var member in xml.Root.Element("members")!.Elements("member"))
                docs.Add(name + ":" + member.Attribute("name")!.Value, member);
        }
        var text = new StringBuilder("---\ntitle: Configuration options\n---\n\n");
        text.Append("Pure constructor defaults and compiler-generated first-party XML documentation; no host, configuration provider, credential discovery, or database is started.\n\n");
        text.Append("## Core options consumed by the API\n\n");
        text.Append("The API binds `NachosOptions` from `Nachos` (`src/Nachos.Api/Program.cs`). Keys below are .NET configuration paths, not snake_case wire fields. Environment-variable binding uses `__` in place of `:`. Null remains unspecified/inherited, not false.\n\n");
        AppendObject(new NachosOptions(), "Nachos", text, docs);
        text.Append("## SQL provider options\n\n");
        text.Append("`Nachos:SqlServer` is `SqlServerOptions.SectionName`. The API selects SQL when `Nachos:SqlServer:ConnectionString` or Aspire's `ConnectionStrings:nachos` exists; the canonical key wins. It binds the SQL section and then applies the selected connection. A blank, malformed or database-less selected value fails startup rather than falling back. With neither key, it uses in-memory storage. A displayed empty connection string is a pure default, never a discovered connection. These binding facts come from `src/Nachos.Api/Providers/ProviderSelection.cs`; no configuration or database is read by DocsGen.\n\n");
        AppendObject(new SqlServerOptions(), SqlServerOptions.SectionName, text, docs);
        text.Append("## API and AppHost boundaries\n\n");
        text.Append("The API implements NachosKey and Microsoft Entra bearer authentication. `Nachos:Auth:Enabled` defaults to true; false is allowed only in Development. These authentication settings are outside the option types above. Configure the ordered `Nachos:Auth:NachosKey:Keys` ring with `Kid` and `Secret` fields; the first key signs and all configured keys validate. Entra binds from `Nachos:Auth:Entra` and requires explicit workspace grants. The `/v3` gate fails closed for unauthenticated callers. Declaration generation does not exercise authentication or grant authority.\n\n");
        text.Append("AppHost-generated keys, connection references, ports and transient orchestration settings are not these defaults. Signing-key material is not read. Client options and future provider/worker settings are outside this reference. Core reasoning and summary settings do not imply their staged routes are implemented.\n");
        _ = inputs.Read("src/Nachos.Api/Program.cs");
        _ = inputs.Read("src/Nachos.Api/Providers/ProviderSelection.cs");
        _ = inputs.Read("src/Nachos.Api/Auth/AuthenticationRegistration.cs");
        _ = inputs.Read("src/Nachos.Hosting/NachosSigningKeyConfigurationExtensions.cs");
        return text.ToString();
    }

    private static void AppendObject(object value, string prefix, StringBuilder text, Dictionary<string, XElement> docs)
    {
        var type = value.GetType();
        var assemblyPrefix = type.Assembly.GetName().Name + ":";
        var summary = Summary(docs, assemblyPrefix + "T:" + type.FullName);
        if (summary is null && type != typeof(DeriverOptions)) throw new InvalidDataException();
        text.Append("### `").Append(type.Name).Append("` — `").Append(prefix).Append("`\n\n");
        text.Append(summary ?? "No type XML summary; member documentation follows.").Append("\n\n");
        text.Append("| Configuration path | Type | Default | XML summary |\n| --- | --- | --- | --- |\n");
        var nested = new List<(object Value, string Prefix)>();
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var member = property.GetValue(value);
            var path = prefix + ":" + property.Name;
            var scalar = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (scalar != typeof(bool) && scalar != typeof(int) && scalar != typeof(string))
            {
                if (member is null || scalar.Assembly != typeof(NachosOptions).Assembly &&
                    scalar.Assembly != typeof(ReasoningConfiguration).Assembly) throw new InvalidDataException();
                nested.Add((member, path));
                continue;
            }
            var typeName = scalar == typeof(bool) ? "bool" : scalar == typeof(int) ? "int" : "string";
            if (Nullable.GetUnderlyingType(property.PropertyType) is not null ||
                new NullabilityInfoContext().Create(property).ReadState == NullabilityState.Nullable) typeName += "?";
            text.Append("| `").Append(path).Append("` | `").Append(typeName).Append("` | `")
                .Append(JsonSerializer.Serialize(member)).Append("` | ")
                .Append(Summary(docs, assemblyPrefix + "P:" + type.FullName + "." + property.Name) ?? "No member XML summary.")
                .Append(" |\n");
        }
        text.Append('\n');
        foreach (var child in nested) AppendObject(child.Value, child.Prefix, text, docs);
    }

    private static string? Summary(Dictionary<string, XElement> docs, string id)
    {
        if (!docs.TryGetValue(id, out var member) || member.Element("summary") is not { } summary) return null;
        var clone = new XElement(summary);
        foreach (var see in clone.Descendants("see").ToArray())
            see.ReplaceWith(see.Attribute("cref")?.Value ?? see.Attribute("langword")?.Value ?? see.Value);
        var text = string.Join(' ', clone.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal).Replace("|", "&#124;", StringComparison.Ordinal);
    }
}
