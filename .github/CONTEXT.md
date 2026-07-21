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
- The release workflow pushes the `Tomix.Cli` tool package to nuget.org when the `NUGET_API_KEY` secret is set; the push steps skip silently when it is not (forks).
- Workflows may reference `/samples` for smoke tests.
- Workflows should not require integration-test secrets for normal PR validation.

## Rules

- Keep CI fast for contributors.
- Run `dotnet build` and `dotnet test`.
- Do not require secrets for normal PR validation.
- Put integration tests behind optional/manual workflows.
- Pin third-party actions (anything outside `actions/*`) to a full commit SHA with a `# vX.Y.Z` comment; Dependabot keeps the pins current.
- Never use `pull_request_target` with a checkout of the PR head, and never move secrets into jobs that run on fork PRs.
- The `build` matrix legs in `ci.yml` are required status checks on `main`; renaming the job or matrix keys requires updating the branch ruleset.
