# Mdl.PowerBI

Power BI and Fabric-specific integration.

## Responsibilities

- Workspace lookup.
- Semantic model lookup.
- Power BI/Fabric REST calls.
- Auth integration.
- Refresh APIs.
- Tenant and service-specific behavior.

## Cross-folder dependencies

- Depends on `/src/Mdl.Core`.
- May be used by `/src/Mdl.Provider.Xmla` for Power BI/Fabric connection resolution.
- May be coordinated by `/src/Mdl.App`.
- Must not depend on `/src/Mdl.Cli`.
- Must not contain generic XMLA behavior that belongs in `/src/Mdl.Provider.Xmla`.

## Rules

- Do not store secrets in config files.
- Never print access tokens or client secrets.
- Keep Power BI-specific behavior out of generic XMLA providers.
- Return structured diagnostics for auth, permission, and API errors.

## Test

```bash
dotnet test tests/Mdl.IntegrationTests
```

Integration tests should be skipped when credentials are unavailable.
