namespace Nachos.Core.Context;

/// <summary>A validated <c>GET S/context</c> request.</summary>
/// <param name="Tokens">The budget, 1 to <see cref="ContextBudget.MaxTokens"/>.</param>
/// <param name="IncludeSummary">Whether a summary may stand in for the messages it covers.</param>
/// <param name="Representation">The representation and card to include, or null for neither.</param>
public sealed record SessionContextRequest(int Tokens, bool IncludeSummary, ContextRepresentation? Representation);
