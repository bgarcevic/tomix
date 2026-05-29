# Mdl.Provider.Tmdl

Provider for TMDL folder-based semantic models.

## Responsibilities

- Open local TMDL models.
- Read model metadata.
- Map TMDL-backed models into MDL abstractions.
- Later: support safe writes.

## Cross-folder dependencies

- Depends on `/src/Mdl.Core`.
- May depend on `/src/Mdl.Provider.Tom` for TOM-backed parsing or mapping.
- Used by `/src/Mdl.App` through provider abstractions.
- Must not depend on `/src/Mdl.Cli`.

## Rules

- Start read-only unless the task explicitly requires saving.
- Preserve file formatting where possible.
- Do not make destructive changes without preview or `--save`.
- Do not leak provider-specific types into `Mdl.Core`.

## Test

```bash
dotnet test tests/Mdl.Provider.Tests
```
