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
- Handlers must not assume they own the session lifecycle. A handler that runs through the
  runners takes an `IModelSessionSource` (its `IEnumerable<IModelProvider>` constructor wraps the
  providers in a `OneShotSessionSource`) and never opens, saves implicitly, or disposes a session
  itself. A successful request is committed; a failed or throwing one is rolled back, which on a
  live session restores the model. Check `MutationContext.KeepsEdit`, not `Mode is Save or Stage`,
  when deciding whether an edit is kept or only previewed.
- One command should usually have one handler.
- Live model session ([ADR 0001](../../docs/design/adr-0001-live-model-session.md)):
  handlers must not care whether their session is one-shot or live. The runners
  (`ModelSessionRunner`, `MutationRunner`, and `bpa run`, which drives the lifecycle itself) take
  their session from an `IModelSessionSource`: `OneShotSessionSource` opens and disposes;
  `LiveSessionSource` leases the open session and never disposes it, and serves only the model the
  session holds (`TOMIX_SESSION_MODEL_MISMATCH`). With `LiveSessionSource.Snapshot` set, its
  leases read that published snapshot instead: no transaction, no wait, no writes.
  `DependencyGraph.FromSnapshot` builds one graph per snapshot, so those readers share it. Under a live source, mutations run in
  `MutationMode.Live`, which applies in a transaction without persisting (status `applied`);
  `--save` applies and saves in the same transaction; `--stage`/`--revert` fail with
  `TOMIX_SESSION_STAGE_UNSUPPORTED`. `SaveModelHandler` takes a source too: an in-place save in a
  live session goes through the session's own save, so it records the save point.
  `Session/LiveSessionHandler` holds one client's session commands (status, history, undo/redo,
  and its explicit transaction); a host keeps one per attached client. Planned: `SessionHost` owns the session registry and client
  attach/detach. Front ends (`shell`, `serve`, `mcp`, `ui`) call handlers with the same
  `*Request` records; they never get their own copy of command logic.
- Formatting behavior:
  - DAX formatting is offline: the vendored SQLBI engine behind `Tomix.Core.Dax.DaxFormatter`,
    wrapped by `Format/OfflineDaxFormatterClient` — no network, no rate limits. The formatter
    never changes code: when printing would not preserve the expression's tokens, strings, and
    comments, it reports a failure and the original text comes back.
  - Power Query (M) formatting is offline too, through `Format/OfflineMFormatterClient`; there is
    no network fallback. `Format/M/powerquery-engine.js` is Microsoft's
    powerquery-parser + powerquery-formatter bundled by `/engines/powerquery` and embedded in this
    assembly (`Format/M/PowerQueryEngineBundle`). It is generated; never edit it by hand. Regenerate
    it with `npm ci && npm run build` in `/engines/powerquery` and read that README before changing
    the build: the bundle must stay runnable under Jint (no `minifySyntax`, no Node built-ins).
    `Format/M/PowerQueryEngine` hosts it in-process under Jint (no Node, no host selector): lazy
    one-time evaluation, serialized calls on a large-stack thread, a per-call time budget, and
    timeouts or engine failures returned as `internal` errors (Ctrl-C still rethrows).
    `PowerQueryEngineTests` mirror `smoke.mjs`, so a Jint-incompatible bundle fails `dotnet test`.
- Workspace discovery uses the Power BI REST API (`GET /v1.0/myorg/groups`) via `Connect/PowerBiWorkspaceCatalog`, authenticated with the shared `IAccessTokenProvider` token (same scope as XMLA). Interactive picking lives in the CLI, not here.
- Release discovery uses the GitHub Releases API via `Update/GitHubReleaseSource` behind `Update/IReleaseSource` (unauthenticated, per-request headers on the shared `HttpClient`). The throttled-check cache lives in `Update/UpdateCheckStore`; install-type detection in `Update/InstallationInspector`.
- Connect decision logic lives in `Connect/ConnectPlanHandler` (pure plan/resolve loop: the CLI resolves each reported `ConnectNeed` with a prompt and re-plans), with mirror probing/scaffolding in `Connect/ConnectWorkspaceHandler` and Desktop instance discovery in `Connect/PowerBiDesktopDiscovery`. `ConnectHandler` stays the session/recents state facade.
- `Connect/PowerBiDesktopDiscovery` owns two pieces of Power BI Desktop trivia that are easy to get wrong, and either one alone makes discovery find nothing:
  - The AnalysisServices workspace root **differs per install variant**. Microsoft Store builds live under `%USERPROFILE%\Microsoft\Power BI Desktop Store App\...` — a different base folder *and* product folder than the MSI layout under `%LOCALAPPDATA%\Microsoft\Power BI Desktop\...`. Every known variant is probed; dropping one silently blinds `--local` to that install type.
  - `msmdsrv.port.txt` is **UTF-16LE with no BOM**, so `File.ReadAllText` yields digits interleaved with NUL and `int.TryParse` fails. Ports are parsed from raw bytes.
  Stale port files (msmdsrv does not reliably delete them on shutdown) are filtered by checking for an active TCP listener, failing open if the listener table is unavailable.
- Report names for discovered instances come from `Connect/PowerBiDesktopProcesses`, which reads each `msmdsrv` command line over WMI (`System.Management`, Windows-only, guarded by `OperatingSystem.IsWindows()`) and takes the parent Desktop window title. The `-s` data directory is the join key back to the port file. Best-effort: any failure yields no labels and discovery still returns bare endpoints. A Desktop instance cannot be identified by model name — over XMLA its database is a GUID and its model is always literally `Model` — so the window title is the only label that distinguishes two instances for a user.
- The same lookup reads the parent Desktop's command line for the file it opened (`OpenedFile`). `Connect/PowerBiDesktopProjects` follows a `.pbip` to its report's `definition.pbir` `byPath` model folder, so `refresh` on a PBIP's files runs in the Desktop that has it open (only when no remote target resolves, and only when exactly one instance matches). A project opened from Desktop's File menu leaves no trace on the command line and is not matched.
- A `--local` session is stored as `Server = "localhost:<port>"` with `Local = true`; `State/ActiveModelResolver` resolves it from `Server` and never reads `Local`, so `ConnectHandler.Set` must keep a local-instance endpoint rather than assume `Local` implies a file path.
- `CliConnectionState.ReportName`/`ReportPortFile` cache the Desktop report name for display. `ConnectHandler.Show` revalidates with `PowerBiDesktopDiscovery.StillServes` and clears both fields when it fails, so no caller can render a stale name. `StillServes` requires **both** that the port file still holds this session's port (distinguishing the original instance from a different report that reused the port) and that something is still listening (msmdsrv does not reliably delete its port file on exit) — it must stay consistent with the staleness filter in `DiscoverInstances`. The cache exists because re-reading the window title per invocation costs ~220ms. Ports change on every Desktop restart, so the stale path is the common one.
- The cache is **not** part of the connection contract: `ConnectShowResult`/`ConnectSetResult` serialize a `ToPublic()` projection (which also drops the session-file `Scope`), and `CliStateStore.AddRecentConnection` strips it. `ReportPortFile` is an absolute path inside the user's profile, so it must never reach command output or the recents file. Only the session file holds it.
- Rule engines share `Tomix.Core.Rules.RuleDefinition` and `RuleSeverity`. BPA rules are
  data (`BpaRule`, a Dynamic-LINQ `Expression` evaluated by `Bpa/BpaEngine`); `validate` rules
  are code in `Validate/ModelValidation`, each described by an entry in `Validate/ValidationRules`.
  A validate issue is raised only through its rule (`ValidationRule.Issue`), so the rule owns the
  code and severity; a new rule goes in that catalog and in `docs/error-codes.md` (pinned by
  `RuleDefinitionTests`). `Dax/` is the model-aware DAX analysis those rules build on
  (sites, tokenizer, reference extraction), not a rule engine of its own.
- The user-facing agent skill lives in `/skills/tomix` and is embedded in this assembly
  (`Skills/SkillBundle`); edit it there, never in an installed copy. `Skills/SkillsHandler` installs
  it and stamps `SKILL.md` frontmatter with `metadata.tomix-version` and `metadata.tomix-hash`, so the
  bundle's own frontmatter must not declare `metadata`. `SkillsHandlerTests` pins the embedded copy to
  the folder.
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
