# tests

Automated tests for Tomix.

## Responsibilities

- Unit tests.
- Application handler tests.
- CLI smoke tests.
- Output-contract tests (`Tomix.Cli.Tests/GetLsParityTests` pins the get/ls JSON+CSV contract; `Tomix.App.Tests/PropertyCatalogTests` pins the per-kind property sets).
- Provider fixture tests.
- Optional integration tests.

## Cross-folder dependencies

- `/tests/Tomix.App.Tests` tests `/src/Tomix.App` and `/src/Tomix.Core`.
- `/tests/Tomix.Cli.Tests` tests `/src/Tomix.Cli`, including the JSON/CSV output contracts.
- `/tests/Tomix.Provider.Tom.Tests` tests `/src/Tomix.Provider.*`.

## Rules

- Default `dotnet test` should be fast and deterministic.
- Do not require external services for normal test runs.
- Use integration tests only when credentials are available.
- Update output-contract tests only when output changes intentionally.

## Test

```bash
dotnet test
```
