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
- Stateful filesystem-backed stores (`CliStateStore`, `StagingStore`, `TomixConfigStore`, `BpaUserRuleState`, `UpdateCheckStore`) are built once as `AppServices` by the CLI composition root (`Program.Main`) and injected into handlers; handlers must not construct them ambiently. Mutation handlers receive them as `Mutations/MutationStores`.
- Keep provider-specific details behind interfaces.
- One command should usually have one handler.
- Formatting behavior uses external formatter APIs:
  - DAX formatting uses the SQLBI DaxFormatter API/client from https://github.com/sql-bi/DaxFormatter.
  - Power Query formatting uses the Power Query Formatter API from https://www.powerqueryformatter.com/api.
- Workspace discovery uses the Power BI REST API (`GET /v1.0/myorg/groups`) via `Connect/PowerBiWorkspaceCatalog`, authenticated with the shared `IAccessTokenProvider` token (same scope as XMLA). Interactive picking lives in the CLI, not here.
- Release discovery uses the GitHub Releases API via `Update/GitHubReleaseSource` behind `Update/IReleaseSource` (unauthenticated, per-request headers on the shared `HttpClient`). The throttled-check cache lives in `Update/UpdateCheckStore`; install-type detection in `Update/InstallationInspector`.
- Connect decision logic lives in `Connect/ConnectPlanHandler` (pure plan/resolve loop: the CLI resolves each reported `ConnectNeed` with a prompt and re-plans), with mirror probing/scaffolding in `Connect/ConnectWorkspaceHandler` and Desktop instance discovery in `Connect/PowerBiDesktopDiscovery`. `ConnectHandler` stays the session/recents state facade.
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
