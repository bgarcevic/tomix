# Tomix.App

Application use cases and command handlers.

## Responsibilities

- Implement command behavior.
- Coordinate providers, core services, validation, and output-ready results.
- Convert CLI requests into domain operations.
- Return structured results and diagnostics.

## Cross-folder dependencies

- Depends on `/src/Tomix.Core`.
- May depend on abstractions implemented by `/src/Tomix.Provider.*`.
- Must not depend on `/src/Tomix.Cli`.
- Must not depend on console or command-line libraries.

## Rules

- Do not write console output directly.
- Keep provider-specific details behind interfaces.
- One command should usually have one handler.
- Formatting behavior uses external formatter APIs:
  - DAX formatting uses the SQLBI DaxFormatter API/client from https://github.com/sql-bi/DaxFormatter.
  - Power Query formatting uses the Power Query Formatter API from https://www.powerqueryformatter.com/api.
- BPA default rules should use the bundled `src/Tomix.App/Bpa/Rules/bpa-rules.json` catalog as the offline standard rules.
- BPA rule loading may support selectable upstream Microsoft Analysis Services BestPracticeRules catalogs from https://github.com/microsoft/Analysis-Services/tree/master/BestPracticeRules.
- Keep licensing-sensitive compatibility work free of versioned third-party product names or abbreviations in source, docs, help, and output.

## Naming

- Handlers: `<CommandName>Handler`
- Requests: `<CommandName>Request`
- Results: `<CommandName>Result`

## Test

```bash
dotnet test tests/Tomix.App.Tests
```
