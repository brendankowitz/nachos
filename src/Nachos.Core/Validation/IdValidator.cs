using Nachos.Abstractions;

namespace Nachos.Core.Validation;

public static class IdValidator
{
    public static void Validate(string id, string paramName)
    {
        if (id is null || id.Length is < 1 or > 512 ||
            id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
        {
            throw new NachosValidationException($"{paramName} must be 1–512 ASCII letters, digits, '_' or '-'.");
        }
    }
}
