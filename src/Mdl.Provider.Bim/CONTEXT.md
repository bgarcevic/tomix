# Mdl.Provider.Bim

Provider for `.bim` model files.

## Responsibilities

- Open local BIM files.
- Read model metadata.
- Map BIM-backed models into MDL abstractions.
- Later: support safe writes.

## Cross-folder dependencies

- Depends on `/src/Mdl.Core`.
- May depend on `/src/Mdl.Provider.Tom`.
- Used by `/src/Mdl.App` through provider abstractions.
- Must not depend on `/src/Mdl.Cli`.

## Rules

- Keep file operations explicit and predictable.
- Do not overwrite files unless the command requires `--save`.
- Do not leak TOM or BIM-specific types outside the provider.
- Use sample BIM files for tests.

## Test

```bash
dotnet test tests/Mdl.Provider.Tests
```
