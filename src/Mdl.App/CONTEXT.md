# Mdl.App

Application use cases and command handlers.

## Responsibilities

- Implement command behavior.
- Coordinate providers, core services, validation, and output-ready results.
- Convert CLI requests into domain operations.
- Return structured results and diagnostics.

## Cross-folder dependencies

- Depends on `/src/Mdl.Core`.
- May depend on abstractions implemented by `/src/Mdl.Provider.*`.
- May coordinate `/src/Mdl.Rules` and `/src/Mdl.Testing`.
- Must not depend on `/src/Mdl.Cli`.
- Must not depend on console or command-line libraries.

## Rules

- Do not write console output directly.
- Keep provider-specific details behind interfaces.
- One command should usually have one handler.

## Naming

- Handlers: `<CommandName>Handler`
- Requests: `<CommandName>Request`
- Results: `<CommandName>Result`

## Test

```bash
dotnet test tests/Mdl.App.Tests
```
