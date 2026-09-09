# Tomix.Core

Core domain types and abstractions.

## Responsibilities

- Shared result types.
- Diagnostics.
- Semantic model abstractions.
- Object paths.
- Provider interfaces.
- Common enums and value objects.
- The `--type` vocabulary (`Models/ModelObjectTypeCatalog`) — the single definition of the
  object-kind tokens every `--type` flag accepts (the discovery list) plus the creation tokens
  `add` advertises. The parser and every help/error text derive from it; do not hardcode type
  lists in commands.
- The property descriptor catalog (`Properties/ModelPropertyCatalog`) — the single definition of every model-object property (JSON key, CSV/text header, value extraction, writable/searchable/diffable flags). get, ls, find, diff, and the mutator's error hints all consume it; add or change properties there, never in a command.
- The DAX language engine (`Dax/`) — vendored lexer/parser/classifier (`Dax/Engine/`, see
  `THIRD-PARTY-NOTICES.md`) behind the `DaxLanguage.Classify` highlighting facade and the
  `DaxSyntaxCheck` offline syntax analyzer. Pure BCL, model-agnostic: it knows syntax, never the
  model. Model-aware DAX analysis (reference extraction, validation) lives in `/src/Tomix.App/Dax`.

## Cross-folder dependencies

- Should not depend on any other `Tomix.*` project.
- Other projects may depend on Core.
- Core types should be stable enough for CLI, App, Output, Providers, Rules, and Testing to share.

## Rules

- Must stay dependency-light.
- Do not depend on CLI, App, Output, TOM, Power BI, XMLA, or console libraries.
- Do not include infrastructure code. Shared BCL-only platform primitives belong in
  `/src/Tomix.Platform`.
- Types here should be stable and reusable.

## Test

```bash
dotnet test tests/Tomix.Core.Tests
```
