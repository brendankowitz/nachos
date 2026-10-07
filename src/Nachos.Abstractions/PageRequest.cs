namespace Nachos.Abstractions;

/// <summary>
/// Paging input shared by every list operation. Construction enforces <c>Page &gt;= 1</c> and
/// <c>1 &lt;= Size &lt;= 100</c>, throwing <see cref="NachosValidationException"/> otherwise.
/// </summary>
public sealed record PageRequest(int Page = 1, int Size = 50, bool Reverse = false)
{
    public const int MaxSize = 100;

    public int Page { get; init; } = Page >= 1
        ? Page
        : throw new NachosValidationException("page must be greater than or equal to 1.");

    public int Size { get; init; } = Size is >= 1 and <= MaxSize
        ? Size
        : throw new NachosValidationException($"size must be between 1 and {MaxSize}.");
}