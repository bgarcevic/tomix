# Mdl.Provider.Tom

Adapter around Microsoft Tabular Object Model.

## Responsibilities

- Translate TOM objects into MDL core abstractions.
- Centralize TOM-specific logic.
- Hide TOM implementation details from the rest of the codebase.

## Cross-folder dependencies

- Depends on `/src/Mdl.Core`.
- May be used by `/src/Mdl.Provider.Tmdl`.
- Must not depend on `/src/Mdl.Cli`.
- Must not leak TOM types into `/src/Mdl.Core` or `/src/Mdl.App`.

## Rules

- Return `Mdl.Core` types from public APIs.
- Keep mapping code explicit and tested.
- Treat provider behavior as infrastructure, not domain logic.

## Test

```bash
dotnet test tests/Mdl.Provider.Tests
```
