# Mdl.Output

Output rendering for MDL commands.

## Responsibilities

- Render human-readable output.
- Render JSON output.
- Render CSV output.
- Render CI annotations for GitHub and Azure DevOps.
- Keep output behavior consistent across commands.

## Cross-folder dependencies

- Depends on `/src/Mdl.Core`.
- May render result types from `/src/Mdl.App`.
- Used by `/src/Mdl.Cli`.
- Must not depend on `/src/Mdl.Provider.*` or `/src/Mdl.PowerBI`.

## Rules

- JSON output is a public contract.
- Prefer structured result objects over string parsing.
- Do not perform command behavior here.
- Do not access model providers directly.
- Update golden tests when output changes intentionally.

## Test

```bash
dotnet test tests/Mdl.GoldenTests
```
