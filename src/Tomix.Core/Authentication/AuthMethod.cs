namespace Tomix.Core.Authentication;

/// <summary>
/// How a token was (or should be) acquired. Used to drive the MSAL flow and to
/// label the active session in <c>auth status</c>.
/// </summary>
public enum AuthMethod
{
    Interactive,
    DeviceCode,
    ServicePrincipalSecret,
    ServicePrincipalCertificate,
    ManagedIdentity
}
