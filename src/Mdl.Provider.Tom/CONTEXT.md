# Mdl.Provider.Tom

Adapter around Microsoft Tabular Object Model.

## Responsibilities

- Translate TOM objects into MDL core abstractions.
- Centralize TOM-specific logic.
- Hide TOM implementation details from the rest of the codebase.

## Cross-folder dependencies

- Depends on `/src/Mdl.Core`.
- May be used by `/src/Mdl.Provider.Bim`, `/src/Mdl.Provider.Tmdl`, and `/src/Mdl.Provider.Xmla`.
- Must not depend on `/src/Mdl.Cli`.
- Must not leak TOM types into `/src/Mdl.Core`, `/src/Mdl.App`, or `/src/Mdl.Output`.

## Rules

- Return `Mdl.Core` types from public APIs.
- Keep mapping code explicit and tested.
- Treat provider behavior as infrastructure, not domain logic.

## Test

```bash
dotnet test tests/Mdl.Provider.Tests
```
