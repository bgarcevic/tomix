# Mdl.Provider.Xmla

Provider for remote XMLA semantic models.

## Responsibilities

- Connect to remote tabular models through XMLA.
- Support remote inspection.
- Support DAX query execution.
- Later: support safe remote writes.

## Cross-folder dependencies

- Depends on `/src/Mdl.Core`.
- May depend on `/src/Mdl.Provider.Tom`.
- May use auth/session abstractions from `/src/Mdl.PowerBI` only when the connection is Power BI/Fabric-specific.
- Used by `/src/Mdl.App` through provider abstractions.
- Must not depend on `/src/Mdl.Cli`.

## Rules

- Remote writes must require confirmation or `--force`.
- Do not store secrets.
- Return connection and auth errors as structured diagnostics.
- Keep Power BI-specific lookup logic in `Mdl.PowerBI`.

## Test

```bash
dotnet test tests/Mdl.Provider.Tests
```

Integration tests may require credentials and should be optional.
