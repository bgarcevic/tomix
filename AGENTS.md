# mdl-cli

Open-source CLI for inspecting, validating, querying, testing, and deploying tabular semantic models.

## Tech Stack

- Language: C#
- Runtime: .NET 10
- CLI: `System.CommandLine`
- Testing: xUnit
- Domain: Power BI, Microsoft Fabric, Analysis Services, TMDL, BIM, XMLA
- Architecture: Modular monolith with thin CLI, application handlers, core abstractions, and provider adapters

## Workspaces

- `/src/Mdl.Cli` - CLI entry point, commands, argument parsing, and exit codes
- `/src/Mdl.App` - Application use cases and command handlers
- `/src/Mdl.Core` - Core abstractions, diagnostics, results, and shared domain types
- `/src/Mdl.Output` - Human, JSON, CSV, and CI output renderers
- `/src/Mdl.Provider.*` - Model providers for TOM, TMDL, BIM, XMLA, and related sources
- `/src/Mdl.PowerBI` - Power BI and Fabric-specific APIs, auth, and workspace/model lookup
- `/src/Mdl.Rules` - Built-in validation rules and rule engine
- `/src/Mdl.Testing` - Semantic model test runner
- `/src/Mdl.Plugins` - Future plugin system
- `/tests` - Unit, handler, CLI, golden, provider, and integration tests
- `/samples` - Sample models, rules, tests, and CI workflows
- `/docs` - Architecture, command docs, JSON schemas, ADRs, and detailed contributor context

## Routing

| Task | Go to | Read | Notes |
|------|-------|------|-------|
| Add or change a CLI command | `/src/Mdl.Cli`, `/src/Mdl.App` | - | Keep CLI thin; put behavior in handlers |
| Add domain types, diagnostics, paths, or result models | `/src/Mdl.Core` | - | Core must stay dependency-light |
| Change command output | `/src/Mdl.Output`, `/tests/Mdl.GoldenTests` | - | Preserve JSON contracts |
| Add `.bim`, TMDL, TOM, or XMLA support | `/src/Mdl.Provider.*` | - | Do not leak provider-specific types |
| Add Power BI or Fabric behavior | `/src/Mdl.PowerBI` | - | Do not store secrets in config |
| Add validation rules | `/src/Mdl.Rules` | - | Emit structured diagnostics |
| Add semantic model tests | `/src/Mdl.Testing` | - | Keep tests scriptable and CI-friendly |
| Add or change tests | `/tests` | - | Prefer fast deterministic tests |
| Add documentation or samples | `/docs`, `/samples` | - | Keep examples copy-pasteable |
| Change repo automation | `/.github` | - | Keep CI fast for contributors |

## Naming Conventions

- Projects and namespaces: `Mdl.<Area>`
- Commands: lowercase and short, for example `doctor`, `info`, `ls`, `get`, `find`
- Handlers: `<CommandName>Handler`
- Requests and results: `<CommandName>Request`, `<CommandName>Result`
- Diagnostics: uppercase snake case prefixed with `MDL_`
- Tests: `<TypeOrFeature>Tests`
- Sample folders and docs: kebab-case

## Development Commands

- Build: `dotnet build`
- Test: `dotnet test`
- Run CLI: `dotnet run --project src/Mdl.Cli -- doctor`
- Run JSON output: `dotnet run --project src/Mdl.Cli -- doctor --format json`