using System.Text.Json.Nodes;

namespace Nachos.Core.Configuration;

public interface IConfigurationResolver
{
    /// <summary>
    /// Resolves each non-null value independently, message over session over workspace over global.
    /// Message configuration contributes reasoning only. The result is internal, not a wire DTO.
    /// Constructed JSON is normalized once using the shared strict-data boundary before projection,
    /// including unknown fields, with its default limit of 64 containers. Caller trees are not retained.
    /// </summary>
    ResolvedConfiguration Resolve(JsonObject? workspace, JsonObject? session = null, JsonObject? message = null);
}
