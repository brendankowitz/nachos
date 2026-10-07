namespace Nachos.Abstractions.Filtering;

/// <summary>
/// Canonical column names used by <see cref="FilterNode.Field"/>. Providers map them to their own storage.
/// </summary>
public static class FilterColumns
{
    /// <summary>A workspace, peer or session name: the public, case-sensitive id.</summary>
    public const string Name = "Name";

    /// <summary>A message's public id.</summary>
    public const string PublicId = "PublicId";

    /// <summary>The row's creation timestamp.</summary>
    public const string CreatedAt = "CreatedAt";

    /// <summary>A session's liveness: <c>LifecycleState = Active</c>. Only <see cref="FilterOp.Eq"/>, <see cref="FilterOp.Ne"/>, <see cref="FilterOp.IsNull"/>, <see cref="FilterOp.NotNull"/>.</summary>
    public const string IsActive = "IsActive";

    /// <summary>
    /// On a session: an existential predicate over its <b>active</b> members' peer names (members who left do not
    /// count). <see cref="FilterOp.Eq"/> means "an active member has this peer name", <see cref="FilterOp.In"/> "an
    /// active member's peer name is listed", <see cref="FilterOp.Contains"/>/<see cref="FilterOp.IContains"/> "an
    /// active member's peer name contains the text", <see cref="FilterOp.Ne"/> the negation of <c>Eq</c> (including
    /// sessions with no active members), <see cref="FilterOp.NotNull"/> "has at least one active member" and
    /// <see cref="FilterOp.IsNull"/> "has none". On a message: the sender's peer name (never unset).
    /// </summary>
    public const string PeerId = "PeerId";

    /// <summary>On a message: the name of the session it belongs to.</summary>
    public const string SessionId = "SessionId";

    /// <summary>A message's text content.</summary>
    public const string Content = "Content";

    /// <summary>A message's token count.</summary>
    public const string TokenCount = "TokenCount";

    /// <summary>
    /// The metadata object. Only used by <see cref="ResourceFields"/> to recognize the wire field; filters on it
    /// become <see cref="FilterNode.MetadataPath"/> nodes.
    /// </summary>
    public const string Metadata = "Metadata";
}

/// <summary>The value type of a filterable field. It decides which values and operators are legal.</summary>
public enum FieldType
{
    Text,
    Number,
    Timestamp,
    Boolean,
    Metadata,
}

/// <summary>A filterable field: the canonical <see cref="FilterColumns"/> column and its value type.</summary>
public sealed record FieldDefinition(string Column, FieldType Type);

/// <summary>The filterable fields of each resource, keyed by wire name.</summary>
public static class ResourceFields
{
    private static readonly FieldDefinition NameText = new(FilterColumns.Name, FieldType.Text);
    private static readonly FieldDefinition CreatedAt = new(FilterColumns.CreatedAt, FieldType.Timestamp);
    private static readonly FieldDefinition Metadata = new(FilterColumns.Metadata, FieldType.Metadata);

    private static readonly IReadOnlyDictionary<string, FieldDefinition> Workspace =
        new Dictionary<string, FieldDefinition>(StringComparer.Ordinal)
        {
            ["id"] = NameText,
            ["name"] = NameText,
            ["metadata"] = Metadata,
            ["created_at"] = CreatedAt,
        };

    private static readonly IReadOnlyDictionary<string, FieldDefinition> Peer =
        new Dictionary<string, FieldDefinition>(StringComparer.Ordinal)
        {
            ["id"] = NameText,
            ["peer_id"] = NameText,
            ["metadata"] = Metadata,
            ["created_at"] = CreatedAt,
        };

    private static readonly IReadOnlyDictionary<string, FieldDefinition> Session =
        new Dictionary<string, FieldDefinition>(StringComparer.Ordinal)
        {
            ["id"] = NameText,
            ["session_id"] = NameText,
            ["is_active"] = new(FilterColumns.IsActive, FieldType.Boolean),
            ["peer_id"] = new(FilterColumns.PeerId, FieldType.Text),
            ["metadata"] = Metadata,
            ["created_at"] = CreatedAt,
        };

    private static readonly IReadOnlyDictionary<string, FieldDefinition> Message =
        new Dictionary<string, FieldDefinition>(StringComparer.Ordinal)
        {
            ["id"] = new(FilterColumns.PublicId, FieldType.Text),
            ["session_id"] = new(FilterColumns.SessionId, FieldType.Text),
            ["peer_id"] = new(FilterColumns.PeerId, FieldType.Text),
            ["content"] = new(FilterColumns.Content, FieldType.Text),
            ["token_count"] = new(FilterColumns.TokenCount, FieldType.Number),
            ["created_at"] = CreatedAt,
            ["metadata"] = Metadata,
        };

    /// <summary>The fields a filter on <paramref name="kind"/> may name. Other top-level keys are ignored.</summary>
    public static IReadOnlyDictionary<string, FieldDefinition> For(ResourceKind kind) => kind switch
    {
        ResourceKind.Workspace => Workspace,
        ResourceKind.Peer => Peer,
        ResourceKind.Session => Session,
        ResourceKind.Message => Message,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown resource kind."),
    };
}
