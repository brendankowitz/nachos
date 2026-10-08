namespace Nachos.Core.Configuration;

/// <summary>An internal resolved value and its origin: message, session, workspace or global.</summary>
public sealed record ResolvedValue<T>(T Value, string Source);

/// <summary>Internal configuration with leaf-level provenance; never serialized as a public resource.</summary>
public sealed record ResolvedConfiguration(
    ResolvedReasoning Reasoning,
    ResolvedPeerCard PeerCard,
    ResolvedSummary Summary,
    ResolvedDream Dream,
    ResolvedDialectic Dialectic,
    ResolvedValue<string?> CustomInstructions,
    ResolvedValue<int> MaxCustomInstructionsTokens);

public sealed record ResolvedReasoning(
    ResolvedValue<bool?> Enabled,
    ResolvedValue<string?> CustomInstructions);

public sealed record ResolvedPeerCard(
    ResolvedValue<bool?> Use,
    ResolvedValue<bool?> Create,
    ResolvedValue<string?> CustomInstructions);

public sealed record ResolvedSummary(
    ResolvedValue<bool?> Enabled,
    ResolvedValue<int> MessagesPerShortSummary,
    ResolvedValue<int> MessagesPerLongSummary,
    ResolvedValue<int> MaxTokensShort,
    ResolvedValue<int> MaxTokensLong,
    ResolvedValue<string?> CustomInstructions);

public sealed record ResolvedDream(
    ResolvedValue<bool?> Enabled,
    ResolvedValue<string?> CustomInstructions);

public sealed record ResolvedDialectic(ResolvedValue<string?> CustomInstructions);
