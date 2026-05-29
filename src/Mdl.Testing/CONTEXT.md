# Mdl.Testing

Semantic model test runner.

## Responsibilities

- Load test definitions.
- Execute DAX-based assertions.
- Compare actual and expected results.
- Produce test results for humans, JSON, and CI.

## Cross-folder dependencies

- Depends on `/src/Mdl.Core`.
- May be coordinated by `/src/Mdl.App`.
- May use query/session abstractions implemented by `/src/Mdl.Provider.Xmla`.
- Must not depend on `/src/Mdl.Cli`.

## Rules

- Tests must be scriptable and deterministic.
- Failed semantic tests should use the documented test failure exit code.
- Do not mix test runner logic with CLI parsing.
- Keep test formats simple and readable.

## Test

```bash
dotnet test
```
