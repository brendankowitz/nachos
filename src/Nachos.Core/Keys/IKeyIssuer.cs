namespace Nachos.Core.Keys;

public interface IKeyIssuer
{
    /// <exception cref="Nachos.Abstractions.NachosValidationException">Keys are absent or claims have invalid scope.</exception>
    string Issue(NachosKeyClaims claims);

    /// <exception cref="Nachos.Abstractions.AuthException">The signature, lifetime or claims are invalid.</exception>
    NachosKeyClaims Validate(string token);
}
