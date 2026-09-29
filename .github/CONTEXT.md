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
- Release flow: `release-pr.yml` keeps a `release/next` PR open (version from `scripts/release/next-version.sh`, notes from `[Unreleased]` via `scripts/release/changelog.sh`); merging it tags `vX.Y.Z` and dispatches `release.yml`. GITHUB_TOKEN pushes do not start workflows, so it dispatches `ci.yml` (required checks) and `release.yml` explicitly. It needs "Allow GitHub Actions to create and approve pull requests".
- `release.yml`: plan → test + build archives + pack → verify every archive on a native runner → publish. Pushes to `main` publish a NuGet preview only (no GitHub Release, so `tx update` never sees it). Major bumps wait on the `major-release` environment, which must have required reviewers.
- `pr-checks.yml` enforces conventional PR titles (the bump source), a `breaking-approved` label for breaking changes, and a `CHANGELOG.md` entry for `src/` changes (`skip-changelog` label opts out).
- The release workflow pushes the `Tomix.Cli` tool package to nuget.org via Trusted Publishing (OIDC): `NuGet/login` exchanges the job's GitHub OIDC token for a short-lived API key, authorized by a policy on nuget.org (owner `bgarcevic`, repo `tomix-cli`, workflow file `release.yml`). The push steps run only when the `NUGET_USER` secret (nuget.org profile name) is set and skip silently when it is not (forks).
- `aot-preview.yml` is an experiment, not a release path: it builds a Native AOT `tx` (win-x64, linux-x64), smoke-tests offline commands against `/samples`, times startup against the single-file build, and uploads the binary as an artifact. It runs on manual dispatch or when the workflow file itself changes, and never fails on smoke results.
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
