# Tomix.Provider.Tmdl

Provider for TMDL folder-based semantic models.

## Responsibilities

- Open local TMDL models.
- Read model metadata.
- Map TMDL-backed models into tomix abstractions.
- Later: support safe writes.

## Cross-folder dependencies

- Depends on `/src/Tomix.Core`.
- May depend on `/src/Tomix.Provider.Tom` for TOM-backed parsing or mapping.
- Used by `/src/Tomix.App` through provider abstractions.
- Must not depend on `/src/Tomix.Cli`.

## Rules

- Start read-only unless the task explicitly requires saving.
- Preserve file formatting where possible.
- Do not make destructive changes without preview or `--save`.
- Do not leak provider-specific types into `Tomix.Core`.

## Test

```bash
dotnet test tests/Tomix.Provider.Tmdl.Tests
```
