# tomix-cli

Open-source CLI for inspecting, validating, querying, testing, and deploying tabular semantic models.

## Tech Stack

- Language: C#
- Runtime: .NET 10
- CLI: `System.CommandLine`
- Testing: xUnit
- Domain: Power BI, Microsoft Fabric, Analysis Services, TMDL, BIM, XMLA
- Architecture: Modular monolith with thin CLI, application handlers, core abstractions, and provider adapters

## Workspaces

- `/src/Tomix.Cli` - CLI entry point, commands, argument parsing, exit codes, and output rendering (`Output/`)
- `/src/Tomix.App` - Application use cases and command handlers
- `/src/Tomix.Core` - Core abstractions, diagnostics, results, and shared domain types
- `/src/Tomix.Provider.*` - Model providers for TOM and TMDL
- `/tests` - Unit, handler, CLI, golden, provider, and integration tests
- `/samples` - Sample models, rules, tests, and CI workflows
- `/docs` - Architecture, command docs, JSON schemas, ADRs, and detailed contributor context

## Routing

| Task | Go to | Read | Notes |
|------|-------|------|-------|
| Add or change a CLI command | `/src/Tomix.Cli`, `/src/Tomix.App` | `CONTEXT.md` in each folder | Keep CLI thin; put behavior in handlers |
| Change command output or color styling | `/src/Tomix.Cli/Output` | `Output/CONTEXT.md`, `/docs/cli-color-strategy.md` | Use `Styling` helpers; do not hard-code ANSI |
| Add domain types, diagnostics, paths, or result models | `/src/Tomix.Core` | `CONTEXT.md` | Core must stay dependency-light |
| Change command output | `/src/Tomix.Cli/Output`, `/tests/Tomix.GoldenTests` | `CONTEXT.md` in relevant folders | Preserve JSON contracts |
| Add TMDL or TOM support | `/src/Tomix.Provider.*` | Provider `CONTEXT.md` files | Do not leak provider-specific types |
| Add or change tests | `/tests` | `CONTEXT.md` | Prefer fast deterministic tests |
| Add documentation or samples | `/docs`, `/samples` | `CONTEXT.md` in each folder | Keep examples copy-pasteable |
| Change repo automation | `/.github` | `CONTEXT.md` | Keep CI fast for contributors |
| Migrate Console.WriteLine to Spectre | `/src/Tomix.Cli/Commands` | `/docs/spectre-migration.md` | Follow migration tracker phases |
| Change the color palette or message categories | `/src/Tomix.Cli/Output/Styling.cs` | `/docs/cli-color-strategy.md` | Update palette in one place only |

## Local Context Files

- Each major folder has a `CONTEXT.md` with local responsibilities, rules, tests, and cross-folder dependencies.
- Read the relevant `CONTEXT.md` before changing files in that area.
- Follow the `Cross-folder dependencies` section to avoid circular dependencies and provider-specific leaks.
- Keep `CONTEXT.md` files lightweight and update them when folder responsibilities or dependency rules change.

## Naming Conventions

- Projects and namespaces: `Tomix.<Area>`
- Commands: lowercase and short, for example `doctor`, `info`, `ls`, `get`, `find`
- Handlers: `<CommandName>Handler`
- Requests and results: `<CommandName>Request`, `<CommandName>Result`
- Diagnostics: uppercase snake case prefixed with `TOMIX_`
- Tests: `<TypeOrFeature>Tests`
- Sample folders and docs: kebab-case

## Commit Conventions

- When asked to commit, also update `CHANGELOG.md`: add a bullet under `[Unreleased]` in the appropriate section (`Added`, `Changed`, `Fixed`, `Removed`). Do not touch version headers — versions come from git tags via MinVer.
- If `CHANGELOG.md` has no `[Unreleased]` section, add one before the latest version header.

## Development Commands

- Build: `dotnet build`
- Test: `dotnet test`
- Run CLI (dev, short + always fresh): `./tx doctor` (`.\tx.ps1 doctor` on Windows) — wraps `dotnet run`, no install needed; reflects current source on every call
- Run CLI (dev, explicit): `dotnet run --project src/Tomix.Cli -- doctor`
- Run JSON output (dev): `./tx doctor --output-format json`
- Install/update global tool: `./scripts/install-dev.ps1` (Windows) or `./scripts/install-dev.sh` (macOS/Linux) — packs and installs `tx` globally so you can run `tx <command>` directly (re-run after each change to pick up edits)
