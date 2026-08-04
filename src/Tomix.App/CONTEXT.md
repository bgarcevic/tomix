# Tomix.App

Application use cases and command handlers.

## Responsibilities

- Implement command behavior.
- Coordinate providers, core services, validation, and output-ready results.
- Convert CLI requests into domain operations.
- Return structured results and diagnostics.

## Cross-folder dependencies

- Depends on `/src/Tomix.Core` for domain contracts and `/src/Tomix.Platform` for shared,
  dependency-free filesystem primitives.
- Receives provider implementations through Core abstractions; never references concrete provider projects.
- Must not depend on `/src/Tomix.Cli`.
- Must not depend on console or command-line libraries.
- Cross-feature composition is allowed when a use case intentionally reuses another application
  capability. Keep those dependencies acyclic; if reuse grows beyond direct orchestration or a
  handler starts serving as a general-purpose service, extract the shared behavior into a clearly
  named capability or shared folder instead of expanding command-to-command coupling.

## Rules

- Do not write console output directly.
- Stateful filesystem-backed stores (`CliStateStore`, `StagingStore`, `TomixConfigStore`, `BpaUserRuleState`, `UpdateCheckStore`) are built once as `AppServices` by the CLI process composition root (`Program.Main`). Command modules select and inject the exact stores their handlers require; handlers never receive the `AppServices` bundle and must not construct stores ambiently. Mutation handlers receive their narrow state dependencies as `Mutations/MutationStores`.
- Keep provider-specific details behind interfaces.
- Standard single-model read handlers use `Models/ModelSessionRunner` for provider resolution,
  guarded session opening, and disposal. Specialized multi-session or staging lifecycles may stay
  explicit when they have different operation-level diagnostics.
- One command should usually have one handler.
- Formatting behavior uses external formatter APIs:
  - DAX formatting uses the SQLBI DaxFormatter API/client from https://github.com/sql-bi/DaxFormatter.
  - Power Query formatting uses the Power Query Formatter API from https://www.powerqueryformatter.com/api.
- Workspace discovery uses the Power BI REST API (`GET /v1.0/myorg/groups`) via `Connect/PowerBiWorkspaceCatalog`, authenticated with the shared `IAccessTokenProvider` token (same scope as XMLA). Interactive picking lives in the CLI, not here.
- Release discovery uses the GitHub Releases API via `Update/GitHubReleaseSource` behind `Update/IReleaseSource` (unauthenticated, per-request headers on the shared `HttpClient`). The throttled-check cache lives in `Update/UpdateCheckStore`; install-type detection in `Update/InstallationInspector`.
- Connect decision logic lives in `Connect/ConnectPlanHandler` (pure plan/resolve loop: the CLI resolves each reported `ConnectNeed` with a prompt and re-plans), with mirror probing/scaffolding in `Connect/ConnectWorkspaceHandler` and Desktop instance discovery in `Connect/PowerBiDesktopDiscovery`. `ConnectHandler` stays the session/recents state facade.
- `Connect/PowerBiDesktopDiscovery` owns two pieces of Power BI Desktop trivia that are easy to get wrong, and either one alone makes discovery find nothing:
  - The AnalysisServices workspace root **differs per install variant**. Microsoft Store builds live under `%USERPROFILE%\Microsoft\Power BI Desktop Store App\...` — a different base folder *and* product folder than the MSI layout under `%LOCALAPPDATA%\Microsoft\Power BI Desktop\...`. Every known variant is probed; dropping one silently blinds `--local` to that install type.
  - `msmdsrv.port.txt` is **UTF-16LE with no BOM**, so `File.ReadAllText` yields digits interleaved with NUL and `int.TryParse` fails. Ports are parsed from raw bytes.
  Stale port files (msmdsrv does not reliably delete them on shutdown) are filtered by checking for an active TCP listener, failing open if the listener table is unavailable.
- Report names for discovered instances come from `Connect/PowerBiDesktopProcesses`, which reads each `msmdsrv` command line over WMI (`System.Management`, Windows-only, guarded by `OperatingSystem.IsWindows()`) and takes the parent Desktop window title. The `-s` data directory is the join key back to the port file. Best-effort: any failure yields no labels and discovery still returns bare endpoints. A Desktop instance cannot be identified by model name — over XMLA its database is a GUID and its model is always literally `Model` — so the window title is the only label that distinguishes two instances for a user.
- A `--local` session is stored as `Server = "localhost:<port>"` with `Local = true`; `State/ActiveModelResolver` resolves it from `Server` and never reads `Local`, so `ConnectHandler.Set` must keep a local-instance endpoint rather than assume `Local` implies a file path.
- `CliConnectionState.ReportName`/`ReportPortFile` cache the Desktop report name for display. `ConnectHandler.Show` revalidates with `PowerBiDesktopDiscovery.StillServes` and clears both fields when it fails, so no caller can render a stale name. `StillServes` requires **both** that the port file still holds this session's port (distinguishing the original instance from a different report that reused the port) and that something is still listening (msmdsrv does not reliably delete its port file on exit) — it must stay consistent with the staleness filter in `DiscoverInstances`. The cache exists because re-reading the window title per invocation costs ~220ms. Ports change on every Desktop restart, so the stale path is the common one.
- The cache is **not** part of the connection contract: `ConnectShowResult`/`ConnectSetResult` serialize a `WithoutReportCache()` projection, and `CliStateStore.AddRecentConnection` strips it. `ReportPortFile` is an absolute path inside the user's profile, so it must never reach command output or the recents file. Only the session file holds it.
- BPA default rules use the embedded `Bpa/Rules/bpa-rules.json` catalog as the single offline source.
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
