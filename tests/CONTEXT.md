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

- `/tests/Tomix.Core.Tests` tests Core types and production project-dependency boundaries.
- `/tests/Tomix.App.Tests` tests App handlers, shared platform primitives, authentication, and
  cross-provider application flows.
- `/tests/Tomix.Cli.Tests` tests `/src/Tomix.Cli`, including the JSON/CSV output contracts.
- `/tests/Tomix.Provider.Tom.Tests` tests the TOM file/server adapter.
- `/tests/Tomix.Provider.Tmdl.Tests` tests TMDL model opening and mapping.
- `/tests/Tomix.Provider.Vpax.Tests` tests VPAX import/export and statistics mapping.

## Rules

- Default `dotnet test` should be fast and deterministic.
- Do not require external services for normal test runs.
- Use integration tests only when credentials are available.
- Update output-contract tests only when output changes intentionally.

## Test

```bash
dotnet test
```
