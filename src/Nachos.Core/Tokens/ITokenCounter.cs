namespace Nachos.Core.Tokens;

/// <summary>Counts content tokens without adding chat framing.</summary>
public interface ITokenCounter
{
    int Count(string text);
}
