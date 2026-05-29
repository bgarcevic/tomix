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
- May depend on `/src/Mdl.Output` for rendering.
- Must not depend directly on `/src/Mdl.Provider.*`, `/src/Mdl.PowerBI`, `/src/Mdl.Rules`, or `/src/Mdl.Testing` unless wired through App-level abstractions.

## Rules

- Keep command classes thin.
- Do not put business logic here.
- Do not access TOM, Power BI, XMLA, BIM, or TMDL APIs directly.
- Do not hand-roll JSON output inside commands.

## Test

```bash
dotnet build
dotnet test
dotnet run --project src/Mdl.Cli -- doctor
```
