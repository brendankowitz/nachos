namespace Nachos.Core.Idempotency;

/// <summary>The original transaction's status and JSON body; replay must not reserialize the body.</summary>
public sealed record CapturedResponse(int Status, string Body);
