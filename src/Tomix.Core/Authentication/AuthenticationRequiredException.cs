namespace Tomix.Core.Authentication;

/// <summary>
/// Thrown when a token is needed but none is cached and it cannot be acquired silently.
/// Lives in Core so the application layer can catch it and map it to the
/// <c>TOMIX_AUTH_REQUIRED</c> diagnostic without referencing the MSAL implementation.
/// </summary>
public sealed class AuthenticationRequiredException : Exception
{
    public AuthenticationRequiredException(string message)
        : base(message)
    {
    }
}
