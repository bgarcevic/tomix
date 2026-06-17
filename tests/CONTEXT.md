# tests

Automated tests for Tomix.

## Responsibilities

- Unit tests.
- Application handler tests.
- CLI smoke tests.
- Golden output tests.
- Provider fixture tests.
- Optional integration tests.

## Cross-folder dependencies

- `/tests/Tomix.Core.Tests` tests `/src/Tomix.Core`.
- `/tests/Tomix.App.Tests` tests `/src/Tomix.App` and may use `/src/Tomix.Core`.
- `/tests/Tomix.Cli.Tests` tests `/src/Tomix.Cli`.
- `/tests/Tomix.GoldenTests` protects output from `/src/Tomix.Cli`.
- `/tests/Tomix.Provider.Tests` tests `/src/Tomix.Provider.*`.

## Rules

- Default `dotnet test` should be fast and deterministic.
- Do not require external services for normal test runs.
- Use integration tests only when credentials are available.
- Update golden tests only when output changes intentionally.

## Test

```bash
dotnet test
```
