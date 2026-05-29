# Mdl.Core

Core domain types and abstractions.

## Responsibilities

- Shared result types.
- Diagnostics.
- Semantic model abstractions.
- Object paths.
- Provider interfaces.
- Common enums and value objects.

## Cross-folder dependencies

- Should not depend on any other `Mdl.*` project.
- Other projects may depend on Core.
- Core types should be stable enough for CLI, App, Output, Providers, Rules, and Testing to share.

## Rules

- Must stay dependency-light.
- Do not depend on CLI, App, Output, TOM, Power BI, XMLA, or console libraries.
- Do not include infrastructure code.
- Types here should be stable and reusable.

## Test

```bash
dotnet test tests/Mdl.Core.Tests
```
