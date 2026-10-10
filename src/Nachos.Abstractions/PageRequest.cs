using Nachos.Abstractions.Contracts;

namespace Nachos.Abstractions;

/// <summary>
/// Paging input shared by every list operation. <c>Page</c> must be at least 1 and <c>Size</c> must be between 1
/// and <see cref="MaxSize"/>. The rules are enforced by the property setters, so constructors, <c>with</c>
/// expressions and object initializers cannot produce an invalid instance.
/// </summary>
/// <exception cref="RequestValidationException">
/// A value is out of range. The error uses FastAPI-style <c>loc</c> (<c>["query","page"]</c> or
/// <c>["query","size"]</c>) so the API can return it as the 422 body unchanged.
/// </exception>
public sealed record PageRequest(int Page = 1, int Size = PageRequest.DefaultSize, bool Reverse = false)
{
    public const int DefaultSize = 50;

    public const int MaxSize = 100;

    /// <summary>1-based page number.</summary>
    public int Page { get; init => field = CheckPage(value); } = CheckPage(Page);

    /// <summary>Items per page, 1 to <see cref="MaxSize"/>.</summary>
    public int Size { get; init => field = CheckSize(value); } = CheckSize(Size);

    private static int CheckPage(int page) =>
        page >= 1
            ? page
            : throw Invalid("page", "Input should be greater than or equal to 1", "greater_than_equal");

    private static int CheckSize(int size) =>
        size switch
        {
            < 1 => throw Invalid("size", "Input should be greater than or equal to 1", "greater_than_equal"),
            > MaxSize => throw Invalid("size", $"Input should be less than or equal to {MaxSize}", "less_than_equal"),
            _ => size,
        };

    private static RequestValidationException Invalid(string field, string message, string type) =>
        new([new ValidationError(["query", field], message, type)]);
}