# Mdl.Cli

CLI entry point for `mdl`.

## Responsibilities

- Define commands, options, arguments, help text, and aliases.
- Parse CLI input using `System.CommandLine`.
- Call application handlers in `Mdl.App`.
- Return documented exit codes.

## Cross-folder dependencies

- Depends on `/src/Mdl.App` for command behavior.
- Depends on `/src/Mdl.Core` for shared result and diagnostic types.
- Renders output in `Output/` (see Structure below).
- Must not depend directly on `/src/Mdl.Provider.*` unless wired through App-level abstractions.

## Structure

- `Commands/` - one `ICommandModule` per command. Each module builds its `Command`, parses input,
  calls a handler, and renders the result. `Program` just registers every module on the root command.
- `Output/` - shared output wiring used by every command:
  - `OutputFormats` - the canonical `--format` option, aliases, and allowed values.
  - `JsonOutput` - the single JSON serializer (the `--format json` contract).
  - `CommandOutput` - format validation, human/JSON dispatch, diagnostic printing, exit-code mapping.

## Rules

- Keep command classes thin.
- Do not put business logic here.
- Do not access TOM, Power BI, XMLA, BIM, or TMDL APIs directly.
- Do not hand-roll JSON output inside commands; serialize through `Output/JsonOutput`.
- Add a new command as its own `ICommandModule` in `Commands/`; reuse `Output/` rather than re-deriving format handling.

## Test

```bash
dotnet build
dotnet test
dotnet run --project src/Mdl.Cli -- doctor
```
