# Tomix.Auth

MSAL-backed authentication and credential caching.

## Responsibilities

- Resolve MSAL client and authority settings.
- Implement Core's `IAuthenticator` and `IAccessTokenProvider` contracts.
- Acquire and refresh interactive, device-code, service-principal, and managed-identity tokens.
- Persist authentication state and credentials using platform-appropriate protection.

## Cross-folder dependencies

- Depends on `/src/Tomix.Core` for authentication contracts and `/src/Tomix.Platform` for shared,
  dependency-free filesystem primitives.
- Constructed by `/src/Tomix.Cli`, the composition root.
- Must not be referenced by `/src/Tomix.App` or provider projects.

## Rules

- Keep MSAL types inside this project.
- Never expose secrets through command arguments, logs, or result types.
- Translate missing or expired credentials to Core's `AuthenticationRequiredException`.
- Keep settings precedence in `AuthSettingsFactory`.

## Test

Authentication and credential-store tests currently live in `tests/Tomix.App.Tests` so the
solution does not carry another small test project.

```bash
dotnet test tests/Tomix.App.Tests --filter "MsalAuthenticatorFastFailTests|CredentialStoreTests"
```
