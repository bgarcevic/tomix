# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

See [docs/cli-ux-guidelines.md](docs/cli-ux-guidelines.md) for the versioning policy
and the API surface that major versions protect.

## [Unreleased]

### Fixed

- `set` error hints now list the writable properties for every kind the catalog models, not
  just tables, measures, columns, and partitions: KPIs (`targetExpression`, `statusExpression`,
  `trendExpression`, `targetFormatString`, `description`), table permissions (`filterExpression`),
  hierarchies, expressions, and functions previously rejected an unknown property with no
  "Writable properties:" hint. A typo like `tx set "Sales/Total/KPI" statusGraphic=Foo` now
  suggests what is actually settable (#144).
- The catalog no longer advertises `name` as writable on table permissions: TOM derives the
  name from the referenced table and rejects the assignment, so `tx set` now reports it as
  unsupported (with the valid tokens) instead of surfacing the underlying TOM error (#144).

## [0.1.0] - 2026-08-22

First public release. Since nothing shipped before it, the sections below describe the
surface as released, together with the changes and fixes made during pre-release
development that are worth knowing about if you followed `main`.

### Added

- Remote model support: `connect`, mutate (`add`/`set`/`rm`/`mv`/`replace`), and `deploy`
  over XMLA.
- Best Practice Analyzer built on Dynamic LINQ (70 bundled rules), with structured
  diagnostics, ignore/disable, and external rule collections.
- Shared M expressions and DAX user-defined functions are visible to the read side:
  `tx ls Expressions` / `tx ls Functions` list them, `tx get "Expressions/<name>"` inspects
  them, `tx find` searches their names, expressions, and descriptions, and
  `--type expression|function` filters by kind. Expressions surface `expression`, `kind`,
  `remoteParameterName`, `lineageTag`, and `sourceLineageTag`; functions surface
  `expression`, `isHidden`, `lineageTag`, and `sourceLineageTag` — all writable via
  `tx set`. `tx ls DataSources` resolves as a container keyword too.
- Columns expose their full writable scalar property surface. `tx set` accepts
  `sourceColumn`, `dataType`, `dataCategory`, `summarizeBy`, `sortByColumn` (by sibling
  column name; empty clears), `lineageTag`, `sourceLineageTag`, `isKey`, `isNullable`,
  `isUnique`, `isAvailableInMDX`, `keepUniqueRows`, `encodingHint`, `alignment`,
  `tableDetailPosition`, `isDefaultLabel`, `isDefaultImage`, `displayOrdinal`,
  `sourceProviderType`, and `isDataTypeInferred` in addition to the original five;
  `get`/`ls`/`find` JSON, CSV, and text output carry the matching column properties. This
  also lets `bpa --fix` rules that assign these properties (e.g. `IsAvailableInMDX = false`)
  apply to columns.
- `tx refresh` — triggers a data refresh on a deployed model over XMLA, with
  `--type`, `--table`, `--partition`, `--apply-refresh-policy` / `--skip-refresh-policy`,
  `--effective-date`, `--max-parallelism`, `--dry-run`, `--no-progress`, and `--trace`.
  Targets the active remote connection by default, or the remote workspace-mode secondary
  when the default is local. Live per-table row counts stream from the XMLA `SessionTrace`
  into a Spectre `Live` table, and a final summary reports per-table `Rows`, `Query`,
  `Read`, `Total`, and `Rows/s` plus a roll-up. JSON/CSV output supported.
- `tx vertipaq` — VertiPaq storage statistics for deployed models, built on the
  MIT-licensed sql-bi VertiPaq-Analyzer libraries. Views: columns by size (default),
  `--tables`, `--columns`, `--relationships`, `--partitions`, `--all`; `--stats` model
  summary; `--detail` size breakdown; `--fields <list>` per-view field selection (with a
  relative-size `bar`); `--top <N>`. A positional table name filters to one table.
  `--export <file.vpax>` / `--import <file.vpax>` for offline analysis, `--obfuscate`
  (writes a private `.dict` dictionary). `--annotate` writes `Vertipaq_*` annotations onto
  the model/tables/columns/relationships via the mutation lifecycle (`--save` to persist,
  workspace mirroring included), using the community keys the bundled BPA rules read
  (`Vertipaq_RowCount`, `Vertipaq_Cardinality`, `Vertipaq_RIViolationInvalidRows`). In
  workspace sessions with a local primary, statistics are read from the remote side
  automatically. JSON (stable contract) and single-view CSV output.
- `tx update` — self-update. Detects the install type: a dotnet global tool runs
  `dotnet tool update -g Tomix.Cli`; a standalone binary (install.sh/install.ps1) downloads
  the matching release asset, verifies it against the published `checksums.txt`, and swaps
  the binary in place (Windows-safe rename-then-replace). `tx update --check` previews the
  latest version and the release notes for every version between installed and latest,
  flagging breaking changes (conventional-commit `!` markers, "breaking change" phrases, or
  a major-version bump); it always exits 0 — scripts read `updateAvailable` from
  `--output-format json`. `--version <v>` targets a specific release (downgrades require
  `--yes`).
- Update notice: `tx` checks GitHub Releases for a newer version at most once per 24 hours
  (cached in `~/.tomix/update-check.json`) and prints a one-line notice on stderr after
  commands when an update is available. The notice never delays or fails a command (the
  network refresh runs after the command completes, capped at 2 seconds, all errors
  swallowed) and is suppressed for `json`/`csv` output, `--quiet`, redirected stderr, `CI`
  environments, dev builds, and via `TOMIX_NO_UPDATE_CHECK=1` or the `updateCheck` config
  key (`tx config set updateCheck false`).
- Releases publish the `Tomix.Cli` package to nuget.org, making
  `dotnet tool install -g Tomix.Cli` a real install channel alongside the GitHub Release
  binaries.
- `tx doctor` — a no-network local health report covering config access and validity,
  profiles, sessions, cached auth metadata, providers, terminal capabilities, and cached
  update information. Corrupt-config recovery keeps help/version, doctor, `config paths`,
  and `config init --force` usable.
- Recent connections: every successful `tx connect` records the resolved connection in
  `~/.tomix/recent-connections.json` (most-recent-first, deduped by target, capped at 20,
  shared across sessions). The global `--recent` option (alias `--recents`) is live:
  `tx connect --recent` opens an interactive picker on stderr (or lists the entries when
  prompts are unavailable — `--non-interactive`, redirected stdin, or
  `--output-format json`), and `tx connect --recent <n>` reconnects to the Nth most recent
  directly, validating the target before replacing the active connection.
  `connect --recent --output-format json` emits a `{"connections":[...]}` contract with
  1-based `index` and `lastUsed`. On model-consuming commands (`ls`, `get`, `find`, `deps`,
  `format`, `add`, `set`, `replace`, `rm`, `mv`, `load`, `save`, `stage`, `validate`, `bpa`,
  `script`, `refresh`), `--recent` supplies the model source for that invocation without
  touching the active connection; on `deploy` it picks the deploy source while
  `--server`/`--database` keep addressing the target.
- `tx connect --remote` — interactive server-only connect: pick a workspace from your Power
  BI tenant, then a semantic model, without remembering any names. Requires a TTY and a
  prior `tx auth login`.
- `tx connect <model> -w` (valueless `-w`) — pick the mirror workspace interactively, then
  pick or create the target model. New models default to an autogenerated
  `<model>-dev-<user>` name you can edit.
- Interactive model picker fills any missing piece on a TTY: `tx connect <workspace>` (no
  model) and `tx connect <model> -w <workspace>` (no model) prompt instead of erroring.
  Non-interactive contexts (`--non-interactive`, `--quiet`, redirected input, json/csv
  output) keep the flag-required errors.
- `IWorkspaceCatalog`/`PowerBiWorkspaceCatalog` (Power BI REST `groups` listing) and the
  `IServerCatalog` provider capability (XMLA database enumeration), reusing the existing
  auth token.
- `tx bpa run --trx <path>` writes a VSTEST `.trx` file with one failed test per violated
  rule (the message lists the violating objects), an error-outcome test per rule that failed
  to compile or evaluate, and a single passed summary test on a clean run. `tx validate
  --trx` emits real per-issue results (errors → Failed, analyzer warnings → Warning
  outcome). Both are ingestible by Azure DevOps `PublishTestResults`.
- `tx set -q name` and `tx mv` warn when a rename leaves DAX expressions referencing the old
  name, listing the referencing objects (renames never rewrite dependent DAX, so the
  breakage was previously silent until a deploy). JSON output gains an optional
  `brokenReferences` field. `--strict-refs` fails the rename instead
  (`TOMIX_RENAME_BREAKS_REFS`, exit 1) so CI can gate on it.
- `tx add` infers the object type from path keywords (`tables/Sales/measures/Revenue`),
  making `-t` optional for the common forms, and extends that inference to `calcgroups/`,
  `calcitems/`, `expressions/`, `functions/`, `calendars/`, and `kpis/`. (`datasources/`
  still requires `-t` — Provider vs Structured is ambiguous.) Matches the convention used by
  `ls`/`get`.
- `tx add` creates relationships: `tx add "Sales[Key]->Product[Key]"` (many side → one
  side), with optional `-t Relationship` or a `relationships/` path prefix. Properties like
  `isActive` and `crossFilteringBehavior` apply via `-q`/`-i`.
- `tx add -t PolicyRangePartition` accepts `--range-start`, `--range-end` (yyyy-MM-dd, both
  required) and `--range-granularity` (Day/Month/Quarter/Year) instead of a hardcoded
  2020–2021 range.
- `tx add --source-schema` sets the schema on an EntityPartition.
- `tx add -t` accepts long-form type aliases: `CalculatedTable`, `CalculatedColumn`,
  `CalculationGroup`, `CalculationItem`, `CalculatedMeasure`.
- `tx set` reaches previously unaddressable object types: relationships (endpoint path
  `Sales[Key]->Product[Key]` or GUID name), named expressions, functions, calculation items,
  cultures, perspectives, data sources, hierarchy levels (`Table/Hierarchy/Level`), and role
  members. Their property handlers existed but no path could resolve them.
- `tx set`/`tx rm` accept container-keyword paths (`tables/Sales/measures/Revenue`,
  `tables/T/partitions/P`), matching `add`/`ls`/`get`.
- `--type` accepts `level`, `calculationitem`/`calcitem`, `member`/`rolemember`, and
  `datasource`.
- `tx replace --in annotations` replaces annotation values across the model, tables,
  columns, measures, hierarchies, partitions, and roles. Explicit-only — `--in all`
  deliberately does not touch annotations (values are often tool-generated JSON).
- `tx find --in formatStrings`, `--in displayFolders`, and `--in annotations` search.
  `formatStrings`/`displayFolders` are included in the default `all` scope; annotations are
  searched only when requested explicitly (models carry hundreds of machine-generated
  `PBI_*` annotations). `--in` values are validated at parse time.
- Selector quoting supports apostrophes in object names: a bare apostrophe is an ordinary
  character (`tx ls "Høreprøver KPI'er"` just works), and inside a quoted segment `''` is a
  literal apostrophe (`'Høreprøver KPI''er'`). A quote only opens a group at the start of a
  segment.
- `tx deps` tracks quoted bare-table references: `COUNTROWS('Udlån')` reports the table as
  upstream. Unquoted bare table names remain untracked (indistinguishable from `VAR` names
  without a DAX parser).
- `tx ls --output-format json` objects include a `path` field, so same-named
  measures/columns in different tables are distinguishable.
- `defaultFormat=text|json` controls implicit command output (`human` is normalized to
  `text`); an explicit `--output-format` always wins.
- Conservative session pruning shares one selector between dry-run and deletion: the default
  removes only dead well-formed PID sessions, while `--all` removes every non-current
  session.
- Diagnostic codes: `TOMIX_INTERACTIVE_REQUIRED`, `TOMIX_REMOTE_LIST_FAILED`,
  `TOMIX_ADD_OPTION_UNSUPPORTED`, `TOMIX_UNKNOWN_OPTION` (exit 2 — an unrecognized
  `--option` that would have been silently bound to a positional argument, e.g.
  `tx ls --bogusflag`, with a did-you-mean suggestion; put `--` before positional values
  that must start with `-`),
  `TOMIX_SAVE_OUTPUT_EXISTS`, and the `TOMIX_UPDATE_*`, `TOMIX_VERTIPAQ_*`, `TOMIX_VPAX_*`,
  and `TOMIX_REFRESH_*` families (`TOMIX_REFRESH_NO_REMOTE_TARGET`,
  `TOMIX_REFRESH_UNSUPPORTED`, `TOMIX_REFRESH_BAD_TYPE`,
  `TOMIX_REFRESH_TABLE_PARTITION_CONFLICT`, `TOMIX_REFRESH_BAD_PARTITION`,
  `TOMIX_REFRESH_FAILED`).
- `OutputExistsException`, plus `Styling.Number(long)` and `Styling.DurationSeconds(double)`
  helpers for human-only output.

### Changed

- **`--output-format json` now emits the documented `{ "data": …, "diagnostics": [] }`
  envelope.** `docs/cli-ux-guidelines.md` has always listed that envelope as API protected by
  major versions, but the code serialized the bare payload — so the contract did not exist.
  Scripts read `.data`; `jq` one-liners in the README and scripting guide are updated to match.
  `diagnostics` ships empty (no handler emits a non-fatal diagnostic yet) and is present so a
  command that later succeeds *with* something to report does not need a breaking change to say
  it. Deliberately **not** enveloped, because they are not command results: `--output-format
  csv`, `get --output-format tmdl|bim|tmsl` (model fragments), `deploy --xmla -` in text mode
  (a TMSL script for the engine — under `--output-format json` it is an ordinary enveloped
  command result), and `query --output-file` (a data file for jq/pandas). Each boundary is
  pinned by a test.
- Project renamed `mdl-cli` → `tomix-cli` (the command is `tx`); MinVer-based versioning and
  CI automation added.
- **Secrets are no longer accepted on the command line or from environment variables**
  (enforcing the policy in `docs/cli-ux-guidelines.md`). `tx auth login --password <value>`
  and `--certificate-password <value>` reject any value other than the `-` stdin sentinel;
  the `AZURE_CLIENT_SECRET` / `TOMIX_AUTH_CLIENT_SECRET` / `TOMIX_AUTH_CERTIFICATE` /
  `TOMIX_AUTH_CERTIFICATE_PASSWORD` environment fallbacks are removed (the non-secret
  `TOMIX_AUTH_CLIENT_ID` / `TOMIX_AUTH_TENANT` remain). Intake channels: `--password-file` /
  `--certificate-password-file`, and a masked interactive prompt when a service-principal
  login omits the secret on a TTY. CI usage:
  `printf '%s' "$SECRET" | tx auth login -u $APP_ID -t $TENANT --password -`.
- Service-principal silent renewal works on macOS/Linux: credentials saved by
  `tx auth login` (default `--save true`) are stored in an owner-only (0600) file under the
  auth directory, replacing the removed environment-variable renewal path. Windows keeps
  DPAPI encryption. A file whose permissions allow group/other access is refused at load.
  Use `--save false` to opt out of persistence.
- The bundled BPA catalog is embedded in the application. A `bpa-rules.json` file beside the
  executable no longer overrides the defaults; use `--rules`, model rule annotations, or
  `bpa rules` for explicit customization.
- Profiles contain connection state only — the inert policy flags/fields (`autoFormat`,
  mutation validation/BPA, deploy BPA, refresh annotations, and spinner) are gone. Desktop
  `Local` and workspace state round-trip.
- Config keys `telemetry`, `activeProfile`, and `hideWarnings` are no longer accepted.
  Existing entries remain on disk and are labeled unsupported by `config show`.
- A failed workspace sync exits 1 instead of 0. Mutation commands with `--save` (and
  `tx save`) still perform the local save and render the result — including the
  `syncWarning` in JSON — but the exit code flags that the mirror was left behind the
  source, so CI can catch the drift. Use `--no-sync` to intentionally skip the mirror
  (exit 0).
- Global `--quiet` has no `-q` alias: `-q` was silently shadowed by the local
  property/query option on `add`/`set`/`get`/`bpa`. Use `--quiet`.
- Exit codes aligned with the documented contract: `TOMIX_NO_PROVIDER`, `TOMIX_NO_MODEL`,
  and `TOMIX_DEPLOY_NO_TARGET` exit 2 (previously 1), and command-line parse errors (unknown
  option, missing argument, invalid option value) exit 2 (previously System.CommandLine's
  default of 1). `tx connect` usage errors (invalid `--workspace`/`--remote` combinations)
  exit 2, matching connect's own `--recent` combination errors.
- Output formats a command cannot render are rejected with exit 2 (`'tx find' does not
  support --output-format csv. Supported: text, json.`) instead of silently falling back to
  text. Enforced by every command: `ls`/`refresh`/`save`/`script` support text/json/csv,
  `get` supports text/json/csv/tmdl/bim/tmsl, and the rest support text/json.
  `--output-format csv` on `diff`/`validate` previously produced their text rendering minus
  a banner line, not CSV — it is rejected too.
- Declining connect's `Overwrite workspace target` confirmation aborts with exit 1 and
  leaves the active connection untouched. Previously the connection was silently saved
  without the workspace and the command exited 0.
- `tx add --revert` combined with `--save-to` errors (`TOMIX_STAGE_OPTIONS_CONFLICT`,
  exit 2) instead of silently dropping the save target. Applies to all mutation commands.
- `tx add` options supplied to a type that ignores them (`--columns` on CalcTable/CalcGroup,
  `--partition-expression` on Entity/PolicyRange partitions, `--connection-string`/`--source`
  on StructuredDataSource, etc.) fail with `TOMIX_ADD_OPTION_UNSUPPORTED` (exit 1) instead
  of exit 0 with the option discarded.
- `tx add --source-database` no longer applies to EntityPartition; use `--source-schema`.
- `tx add -t PolicyRangePartition` requires `--range-start` and `--range-end`.
- Invalid `--mode`, `--serialization`, and `--range-granularity` values on `tx add` are
  rejected at parse time (before any model is opened) instead of at apply time.
  `--serialization` accepts `tmdl`, `bim`, `tmsl`, `auto` (the previously advertised
  `te-folder`/`pbip` were never implemented). Invalid `--serialization` values on
  `set`/`mv`/`rm`/`replace`/`save`/`init`/`script`/`bpa` are rejected at parse time too, and
  help text no longer advertises the unimplemented `te-folder`/`pbip`/`database.json`
  formats (`init` genuinely supports `pbip`).
- A dangling `-q` with no matching `-i` on `tx add` is a usage error (exit 2) instead of
  being silently dropped.
- `tx deps --max-depth` must be at least 1; `0` previously acted as unlimited.
- Invalid `--regex` patterns on `tx find` fail up-front with `TOMIX_FIND_INVALID_REGEX`
  (exit 2) instead of crashing mid-search.
- `tx replace --in <unknown-scope>` errors (`TOMIX_MUTATION_INVALID_VALUE`) instead of
  exiting 0 with nothing replaced.
- `tx add --revert` prints `Reverted.` and an `--if-not-exists` no-op prints
  `Already exists: <path>` instead of the misleading `Added: False` + "Changes not saved"
  warning. JSON output gains optional `reverted`/`existingPath` fields. `tx mv --revert`
  prints `Reverted.` instead of falsely claiming `Renamed: A -> B`; `tx rm --revert` prints
  `Reverted.` and `rm --if-exists` on a missing object prints
  `Not found: <path> (nothing removed)` instead of exiting silently.
- Mutation spinners label the actual operation (`Working...`/`Staging...`/`Reverting...`)
  instead of always `Saving...`.
- `--save` to the source model (in-place) no longer errors with "Output directory already
  exists". In-place saves overwrite cleanly; `--save-to <existing>` still errors unless
  `--force` (mapped to `TOMIX_SAVE_OUTPUT_EXISTS`).
- `tx add`/`set`/`rm`/`mv` help examples use the canonical keyword-path form
  (`tables/Sales/measures/Revenue`) so they are copy-pasteable.
- `refresh` promoted from a compatibility stub to a real command;
  `TomServerModelSession` implements `IModelRefreshSession`, so refresh is supported only on
  sessions connected to a live XMLA endpoint.

### Removed

- `tx interactive` command: the REPL spawned a fresh `tx` process per line, so it had no
  warm connection or cached model — just a worse shell (no tab completion, history, pipes,
  or streamed output). `tx connect` plus shell completions (`tx completion <shell>`) cover
  the workflow; a true in-process REPL can be revisited if a persistent session ever proves
  necessary.
- `tx macro` command and everything around it: the `TOMIX_MACRO_*` error codes, the
  `TOMIX_MACROS_PATH`/`TE_MACROS_PATH` environment variables, and the `macros` config key.
  `macro run` was never implemented, so the catalog could be edited but never executed;
  `tx script` covers running C# against a model.
- `tx info` command (use `tx load` or `tx connect` instead).
- `TOMIX_CONNECT_INVALID_TARGET` error code: the branch was unreachable — any server value
  containing a path separator classifies as a local model path, and the remaining bare names
  always normalize to `powerbi://` (or localhost) endpoints, so `connect` can never plan a
  dead-end target. Unopenable inputs fail at validation with
  `TOMIX_NO_PROVIDER`/`TOMIX_MODEL_LOAD_FAILED`.

### Fixed

- An unreadable model source (e.g. a permission-denied `.pbip`) no longer crashes with the
  generic "Unexpected error / report a bug" fallback. Provider matching treats an unreadable
  candidate as unresolvable (an unreadable `.pbip` still opens when a sibling
  `*.SemanticModel` folder resolves), and a model source that exists on disk but cannot be
  read reports `TOMIX_MODEL_LOAD_FAILED` (exit 2) naming the file, from every command that
  resolves a model. `IModelProvider.CanOpen` is documented as a must-not-throw total
  predicate.
- `tx connect --local` actually connects to a running Power BI Desktop instance. Three
  independent bugs each broke it on their own:
  - Microsoft Store installs were never found. They keep their AnalysisServices workspace
    under `%USERPROFILE%\Microsoft\Power BI Desktop Store App\...`, while only the MSI
    location under `%LOCALAPPDATA%` was probed. All known install variants are now probed.
  - `msmdsrv.port.txt` is UTF-16LE with no BOM, so reading it as text produced digits
    interleaved with NUL characters and **every** port failed to parse — including on MSI
    installs. Ports are now parsed from raw bytes and any of the encodings Desktop may write
    is accepted.
  - The discovered `localhost:<port>` endpoint was discarded when the session was saved, so
    `tx connect --local` reported success but left a session that no later command could
    resolve. The endpoint is now persisted.

  Instances that have since exited are skipped instead of being offered as dead endpoints,
  and filesystem errors while probing report "none found" rather than escaping as an
  unhandled exception.
- `tx connect` shows the report name for a Power BI Desktop session (`Active: Sales Overview
  (localhost:59962)`), since the port alone says nothing about which report is open. The name
  is cached when `--local` connects, then revalidated on every read — so it stays a cheap
  local check rather than a ~220 ms WMI query. Revalidation requires both that the
  instance's `msmdsrv.port.txt` still holds that port and that something is still listening
  on it, so a name is never shown for a report that has been closed or for a different
  instance that reused the port (Desktop picks a new port on each start). The cache is
  internal: it is stripped from command output and from recents.
- `tx connect --local` with several reports open shows a picker labelled by report name
  (listing the instances instead when not on a TTY, so one can be connected to directly with
  `tx connect localhost:<port>`). It previously failed with "Specify a semantic model name" —
  advice it could not honor, because over XMLA a Desktop database is named by a GUID and its
  model is always literally `Model`; a supplied name was ignored and the first instance found
  was used regardless.
- `connect` accepts documented bare workspace names (e.g. `tx connect MyWorkspace Sales`)
  instead of rejecting them; the name is normalized to a fully-qualified `powerbi://`
  endpoint so every later command can open it.
- `connect --workspace` shows a spinner during the remote probe (no silent gap).
- Destructive confirmations (`rm`, `replace`, `deploy`, `update`, `incremental-refresh rm`,
  and connect's workspace-overwrite) fail fast with `TOMIX_CONFIRMATION_REQUIRED` in every
  non-promptable context — `--quiet`, `--output-format json`/`csv`, and redirected
  stdin/stderr — instead of blocking on a prompt (or prompting mid-JSON). Previously only
  `--non-interactive` and redirected stdin were detected; they now share the interaction gate
  used by `session clear`/`prune` and `stage discard`. `--yes` still bypasses, the error
  still goes to stderr, and the prompt still defaults to no.
- In-place `--save` against a remote model (`powerbi://`/`asazure://`) actually persists.
  The remote session saved via parameterless `Database.Update()`, which alters only the
  database object itself — model-tree changes (measures, properties, annotations) were
  silently dropped while the command reported "Saved" (verified live: an annotation write
  survived a fresh connection only after the fix). Remote saves now use
  `Model.SaveChanges()` and surface XMLA errors returned in its result instead of assuming
  success. Affected every mutation command (`set`, `mv`, `rm`, `replace`,
  `vertipaq --annotate`) when connected directly to a workspace; workspace-mirror sync
  (deploy-based) was not affected.
- `--help` exits 0 on every command. Commands with required positional arguments (`mv`,
  `add`, `rm`, `set`, `get`, `find`, ...) printed help but exited 2, because the missing
  arguments still counted as a usage error — breaking `tx <cmd> --help && ...` scripting. The
  Spectre help action now clears parse errors the way the built-in one does; genuinely
  missing arguments (without `--help`) still exit 2.
- Workspace sync with no cached login no longer stalls silently for minutes before warning
  (observed: 4m37s). Token acquisition gates on the recorded login state and fails
  immediately with "Not authenticated. Run 'tx auth login'." — without opening the
  OS-keystore-backed MSAL cache, whose authorization prompt can block a non-interactive
  process — and silent acquisition is capped at 30 s with an actionable timeout error as a
  backstop.
- The live spinner shows `Syncing to <workspace>...` during the workspace-sync phase instead
  of sitting on `Saving...`, and the sync-failure warning explains how to recover (re-push
  with `tx save`, or skip with `--no-sync`).
- `tx mv` destinations are parsed with the same quote- and DAX-aware rules as sources. A
  DAX-form destination (`'Sales'[New]`) previously became the *literal* object name —
  `mv "Sales[a]" "Sales[b]" --save` persisted a column named `Sales[b]` that mv could no
  longer address — and apostrophes in destination names were silently stripped
  (`QA's Measure` → `QAs Measure`). Result paths keep their apostrophes now.
- `tx mv` with a missing object name (empty source/destination, trailing `/`) errors with
  `TOMIX_MOVE_INVALID_PATH` (exit 2) instead of the misleading "Moving objects between
  parents is not supported yet." Identical source and destination error with
  `TOMIX_MOVE_NOOP` instead of reporting a rename that never happened — previously this also
  emitted a false broken-references warning. Case-only renames proceed but skip the
  broken-references warning (DAX resolves names case-insensitively).
- Mutation saves can no longer silently change serialization in place:
  `mv/set/add/rm --save --serialization bim` on a TMDL model wrote a stray `definition.bim`
  inside the PBIP folder, left the real model untouched, and reported "Saved" — now a hard
  error directing to `--save-to`.
- `--revert` with nothing staged fails with `TOMIX_STAGE_NOTHING_STAGED` instead of printing
  `Reverted.` (exit 0) unconditionally.
- `--save-to` no longer deploys the mutation to the connected workspace mirror: it writes a
  copy to a side location while the connected source is untouched, so syncing the mirror
  silently diverged it from the source. `--save-to` on mutation commands honors `--force`
  (no silent overwrite).
- `tx mv --stage` output says `Staged. Run 'tx stage commit' to promote.` — it previously
  claimed "Changes not saved. Use --save to persist", steering users into bypassing the
  stage.
- `--error-format json` is honored by **every** command. It was advertised on all 53 help
  screens but silently ignored by eleven command modules — `bpa`, `config`, `connect`
  (some paths), `doctor`, `init`, `profile`, `replace`, `session`, `stage`, `update`, and
  `validate` — which printed colored text to stderr regardless, breaking any pipeline that
  branched on the error code. The cause was `CommandOutput.Render` overloads whose
  `errorFormat` defaulted to `null`; those overloads are gone, so a command now cannot
  compile without deciding. Earlier rounds of the same bug covered `mv`/`set`/`add`/`rm`
  (mutation errors always printed as text), `tx ls` (text error while `get`/`find`/`deps`
  emitted the JSON envelope), `tx connect` connection-validation failures, and
  `refresh --partition` malformed values (now `TOMIX_REFRESH_BAD_PARTITION` through
  `ErrorOutput` instead of raw text).
- Remote XMLA connections are capped at 30 seconds (`Connect Timeout`), matching the REST
  side. Nothing emitted a timeout of any kind before, so a cold or unreachable Power BI XMLA
  endpoint parked **every** command that reaches a remote model (`info`, `ls`, `get`, `query`,
  `deploy`, `refresh`, `vertipaq`) on its spinner indefinitely — and because `Server.Connect`
  is a blocking call that never observes its cancellation token, Ctrl-C could not break out
  either. Local Power BI Desktop instances are on loopback and stay uncapped. The timeout is
  applied in one place (`XmlaConnectionString`) that all three client paths — the AMO session,
  the deploy target, and VertiPaq extraction — now share, guarded by a test that fails if any
  of them stops routing through it or if a fourth builds its own. Making the call genuinely cancellable is deliberately left for later; it is only
  worth the complexity if a capped wait still feels stuck in practice.
- `tx add -q <property>` with no matching `-i <value>` wrote its error to **stdout** (via
  `AnsiConsole.MarkupLine`), so `tx add … | jq` received colored markup on the data stream
  instead of the empty stream a failed command owes it. It now goes to stderr as
  `TOMIX_ADD_VALUE_REQUIRED`, and a test pins the stream rather than the wording.
- Usage errors that previously wrote bare markup to stderr now carry documented codes and
  honor `--error-format`: an unrecognized `--type` is `TOMIX_INVALID_TYPE`, a destructive
  action that cannot prompt is `TOMIX_CONFIRMATION_REQUIRED`, and mutually-exclusive options
  (`--recent` with a model path, `--profile` with a server/database) are
  `TOMIX_OPTION_CONFLICT`. The wording still differs per command — `deploy --recent --server`
  is legal because the server addresses the deploy target — so the code is the stable part.
- `--output-format json` now implies JSON errors on every command, as
  `docs/error-codes.md` has always documented. Previously only `connect` and `vertipaq`
  derived it, so `tx ls --output-format json` against a bad model emitted JSON on stdout
  and an unparseable text error on stderr. `GlobalOptions.ErrorFormatValue` is the single
  place that resolves the rule.
- `tx mv` rejects `--output-format csv`/`tmdl` (exit 2) instead of silently rendering text;
  `--force` help text matches what it does (gates `--save-to` overwrite).
- `tx add` rejects cross-kind name collisions within a table: measures, columns, and
  hierarchies share a namespace in tabular models, but `add tables/T/measures/X` succeeded
  when a column (or hierarchy) named `X` already existed — writing TMDL the engine rejects at
  deploy. All three collections are checked and the error names the colliding kind.
  `--if-not-exists` still tolerates a same-kind duplicate; a cross-kind squatter remains a
  hard error.
- TMDL saves no longer rewrite every table file of a Power BI Desktop-authored model.
  `TmdlSerializer` indents M partition `source =` bodies two levels below the property while
  Desktop writes them one level deep (they agree on measures, calc items, and DAX/calculated
  partition sources), so any `--save` re-indented every M partition in the folder. The
  exporter post-processes M-partition source blocks to Desktop's depth — a save of an
  untouched Desktop model is byte-identical, and a mutation diffs only the lines it changed.
  The transform is lossless (TMDL strips common leading whitespace of delimited expressions
  on parse) and idempotent.
- `tx set`/`tx rm` DAX bracket paths (`'Table'[Child]`) resolve only to measures and
  columns, like DAX itself. Previously a same-named partition could be silently picked —
  `set 'T'[X] -q expression` would replace the partition's M source query instead of the
  measure's DAX.
- `tx set`/`tx rm` mutation paths with embedded apostrophes resolve, in both
  `'Månedens KPI''er'` (escaped) and raw `Månedens KPI'er` forms, matching the `ls`/`get`
  selector rules.
- Same-name collisions across object kinds (e.g. a measure and a partition both named
  `Budget`) fail with `TOMIX_OBJECT_AMBIGUOUS` and a `--type` hint instead of silently
  mutating whichever kind resolved first. `TOMIX_OBJECT_AMBIGUOUS` errors (`get`, `deps`)
  list up to 5 candidate paths with their kinds and hint `-t <type>` disambiguation, instead
  of only naming the ambiguous path.
- `tx set`/`tx rm` not-found errors emit `TOMIX_OBJECT_NOT_FOUND` with a hint (previously
  generic `TOMIX_MUTATION_FAILED`); unsupported-property errors name the object type that
  actually resolved.
- `tx set --revert` combined with `-q`/`-i` hard-errors (`TOMIX_STAGE_OPTIONS_CONFLICT`,
  exit 2) instead of silently discarding the assignment.
- `tx set --force` help text no longer promises validation-error handling that does not
  exist; it gates `--save-to` overwrite.
- Values read from stdin (`-i -` or piped) no longer keep the trailing newline that
  `echo`/heredoc pipes append.
- `--save` on an existing model directory no longer fails; the directory is cleared and
  rewritten so deleted objects don't leave orphan files.
- Empty `--type` on `tx add` produces an actionable error ("No object type given…") instead
  of `Adding object type '' is not supported yet.`
- `tx deps --quiet` no longer prints "Running semantic analysis...".
- `refresh` per-table `Query`/`Read`/`Total` accuracy — the trace sink maps `ExecuteSql` →
  Query and `ReadData` → Read + row count instead of wrong subclasses. Row counts are
  captured in-flight via `ReadData.IntegerData` (the broken post-refresh DMV query is gone).
- `refresh --trace` as a bare flag resolves to stderr as documented, instead of silently
  doing nothing, and no longer disposes `Console.Error` (wrapped in
  `NonDisposingTextWriter`).
- `refresh` honors an injected connection session in `ActiveModelResolver`, so resolution and
  tests no longer require a live remote session.
- `deploy --fix-bpa` blocks on remaining error-severity violations; unsupported sessions fail
  with `TOMIX_DEPLOY_FIX_UNSUPPORTED`.
- Source resolution honors global `--server`/`--database` on `ls`, `get`, `find`, etc.
- Help fixes: the `get`/`find`/`deps` examples no longer show nonexistent flags (`-t dax`,
  `find --type`, `deps --direction`); the `find` zero-match hint no longer suggests a
  nonexistent option; `ls --type` help lists `calculatedcolumn`; the `--output-format`
  description typo "tTomix" is `tmdl` again.

[Unreleased]: https://github.com/bgarcevic/tomix-cli/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/bgarcevic/tomix-cli/releases/tag/v0.1.0
