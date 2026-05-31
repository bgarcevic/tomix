# tests

Automated tests for MDL.

## Responsibilities

- Unit tests.
- Application handler tests.
- CLI smoke tests.
- Golden output tests.
- Provider fixture tests.
- Optional integration tests.

## Cross-folder dependencies

- `/tests/Mdl.Core.Tests` tests `/src/Mdl.Core`.
- `/tests/Mdl.App.Tests` tests `/src/Mdl.App` and may use `/src/Mdl.Core`.
- `/tests/Mdl.Cli.Tests` tests `/src/Mdl.Cli`.
- `/tests/Mdl.GoldenTests` protects output from `/src/Mdl.Cli`.
- `/tests/Mdl.Provider.Tests` tests `/src/Mdl.Provider.*`.

## Rules

- Default `dotnet test` should be fast and deterministic.
- Do not require external services for normal test runs.
- Use integration tests only when credentials are available.
- Update golden tests only when output changes intentionally.

## Test

```bash
dotnet test
```
