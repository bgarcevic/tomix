# .github

GitHub automation and project metadata.

## Responsibilities

- CI workflows.
- Issue templates.
- Pull request templates.
- Release automation.

## Cross-folder dependencies

- CI should build `/src`.
- CI should test `/tests`.
- Release workflows should package `/src/Tomix.Cli`.
- Workflows may reference `/samples` for smoke tests.
- Workflows should not require integration-test secrets for normal PR validation.

## Rules

- Keep CI fast for contributors.
- Run `dotnet build` and `dotnet test`.
- Do not require secrets for normal PR validation.
- Put integration tests behind optional/manual workflows.
