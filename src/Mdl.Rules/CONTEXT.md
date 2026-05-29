# Mdl.Rules

Validation rules and rule engine.

## Responsibilities

- Built-in model quality rules.
- Rule configuration.
- Rule execution.
- Structured diagnostics.

## Cross-folder dependencies

- Depends on `/src/Mdl.Core`.
- May be coordinated by `/src/Mdl.App`.
- May use provider abstractions from Core, but should not depend directly on provider implementations.
- Must not depend on `/src/Mdl.Cli` or `/src/Mdl.Output`.

## Rules

- Rules should emit diagnostics, not console output.
- Use stable rule IDs.
- Keep rule logic deterministic.
- Avoid building a complex rule language too early.

## Naming

- Rule IDs: uppercase snake case.
- Example: `MDL_MEASURE_DESCRIPTION_MISSING`

## Test

```bash
dotnet test tests/Mdl.App.Tests
```
