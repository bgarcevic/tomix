# Tomix.Cli

CLI entry point for `tx`.

## Responsibilities

- Define commands, options, arguments, help text, and aliases.
- Parse CLI input using `System.CommandLine`.
- Call application handlers in `Tomix.App`.
- Return documented exit codes.

## Cross-folder dependencies

- Depends on `/src/Tomix.App` for command behavior.
- Depends on `/src/Tomix.Core` for shared result and diagnostic types.
- Renders output in `Output/` (see Structure below).
- References `/src/Tomix.Provider.*` projects only so `Program` (the composition root) can
  construct providers and pass them to commands as `IModelProvider` lists. Feature logic must
  go through App/Core abstractions — never use provider-specific types in command modules.

## Structure

- `Commands/` - one `ICommandModule` per command. Each module builds its `Command`, parses input,
  calls a handler, selects an output renderer, and returns the exit code. Commands may render prompts
  and trivial one-line messages; complex tables, trees, serialization, and projections live in `Output/`.
- `Output/` - shared output wiring used by every command. See `Output/CONTEXT.md` for details.
  - `OutputFormats` - the canonical `--format` option, aliases, and allowed values.
  - `JsonOutput` - the single JSON serializer (the `--format json` contract).
  - `CommandOutput` - format validation, human/JSON dispatch, diagnostic printing, exit-code mapping.
  - `Styling` - color palette and markup helpers. Single source of truth for all color/style decisions.

## Rules

- Keep command classes thin.
- Do not put business logic here.
- Do not access TOM, Power BI, XMLA, BIM, or TMDL APIs directly.
- Do not hand-roll JSON output inside commands; serialize through `Output/JsonOutput`.
- Add a new command as its own `ICommandModule` in `Commands/`; reuse `Output/` rather than re-deriving format handling.
- CLI help, JSON field names, diagnostics, and human output must avoid versioned third-party product names or abbreviations for licensing-sensitive compatibility work.
- Use the color palette and helpers in `Output/Styling.cs`. See `/docs/cli-color-strategy.md` for the full palette, message categories, and migration status.
- Do not hard-code ANSI escape codes or Spectre markup strings in commands. Use `Styling` helpers.
- Consult `/docs/cli-ux-guidelines.md` when adding or changing any command, option, argument, help text, output rendering, error message, or exit code.

## Test

```bash
dotnet build
dotnet test
dotnet run --project src/Tomix.Cli -- doctor
```
