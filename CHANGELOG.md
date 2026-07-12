# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

See [docs/cli-ux-guidelines.md](docs/cli-ux-guidelines.md) for the versioning policy
and the API surface that major versions protect.

## [Unreleased]

- Remote model support: `connect`, mutate (`add`/`set`/`rm`/`mv`/`replace`), `deploy` over XMLA.
- BPA engine rewritten with Dynamic-LINQ (70 bundled rules), structured diagnostics, ignore/disable, external rule collections.
- Project renamed `mdl-cli` → `tomix-cli` (`tx` command); MinVer-based versioning and CI automation added.

### Added

- `tx refresh` — triggers data refresh on deployed models via XMLA. Supports `--type`, `--table`, `--partition`, `--apply-refresh-policy`/`--skip-refresh-policy`, `--effective-date`, `--max-parallelism`, `--dry-run`, `--no-progress`, `--trace`. Live per-table progress from XMLA `SessionTrace` with summary table (`Rows`, `Query`, `Read`, `Total`, `Rows/s`). JSON/CSV output support.
- `TOMIX_REFRESH_*` diagnostic codes.
- `Styling.Number(long)` and `Styling.DurationSeconds(double)` helpers.
- `tx add` infers the object type from path keywords (`tables/Sales/measures/Revenue`), making `-t` optional for the common forms. Matches the convention used by `ls`/`get`.
- `OutputExistsException` and `TOMIX_SAVE_OUTPUT_EXISTS` error code for save-target conflicts.
- `tx add` creates relationships: `tx add "Sales[Key]->Product[Key]"` (many side -> one side), with optional `-t Relationship` or a `relationships/` path prefix. Properties like `isActive` and `crossFilteringBehavior` apply via `-q`/`-i`.
- `tx add -t PolicyRangePartition` accepts `--range-start`, `--range-end` (yyyy-MM-dd, both required) and `--range-granularity` (Day/Month/Quarter/Year) instead of a hardcoded 2020–2021 range.
- `tx add --source-schema` sets the schema on an EntityPartition (previously mis-mapped from `--source-database`).
- `tx add -t` accepts long-form type aliases: `CalculatedTable`, `CalculatedColumn`, `CalculationGroup`, `CalculationItem`, `CalculatedMeasure`.
- `tx add` path-keyword inference extended to `calcgroups/`, `calcitems/`, `expressions/`, `functions/`, `calendars/`, and `kpis/`. (`datasources/` still requires `-t` — Provider vs Structured is ambiguous.)
- `TOMIX_ADD_OPTION_UNSUPPORTED` error code: an `add` option supplied for an object type that cannot consume it now hard-errors instead of being silently ignored.
- `tx replace --in annotations` now works: replaces annotation values across the model, tables, columns, measures, hierarchies, partitions, and roles. Explicit-only — `--in all` deliberately does not touch annotations (values are often tool-generated JSON).

### Changed (breaking)

- Global `--quiet` no longer has a `-q` alias: `-q` was silently shadowed by the local property/query option on `add`/`set`/`get`/`bpa`/`macro`. Use `--quiet`.
- `tx add --revert` combined with `--save-to` now errors (`TOMIX_STAGE_OPTIONS_CONFLICT`, exit 2) instead of silently dropping the save target. Applies to all mutation commands.
- `tx add` options supplied to a type that ignores them (`--columns` on CalcTable/CalcGroup, `--partition-expression` on Entity/PolicyRange partitions, `--connection-string`/`--source` on StructuredDataSource, etc.) now fail with `TOMIX_ADD_OPTION_UNSUPPORTED` (exit 1) instead of exit 0 with the option discarded.
- `tx add --source-database` no longer applies to EntityPartition; use `--source-schema` for the schema name.
- `tx add -t PolicyRangePartition` requires `--range-start` and `--range-end`.
- Invalid `--mode`, `--serialization`, and `--range-granularity` values on `tx add` are rejected at parse time (before any model is opened) instead of at apply time. `--serialization` accepts `tmdl`, `bim`, `tmsl`, `auto` (the previously advertised `te-folder`/`pbip` were never implemented).
- A dangling `-q` with no matching `-i` on `tx add` is now a usage error (exit 2) instead of being silently dropped.
- `tx add --revert` prints `Reverted.` and an `--if-not-exists` no-op prints `Already exists: <path>` instead of the misleading `Added: False` + "Changes not saved" warning. JSON output gains optional `reverted`/`existingPath` fields.
- Mutation spinners now label the actual operation (`Working...`/`Staging...`/`Reverting...`) instead of always `Saving...`.
- `tx replace --in <unknown-scope>` now errors (`TOMIX_MUTATION_INVALID_VALUE`) instead of exiting 0 with nothing replaced.
- `tx mv --revert` prints `Reverted.` instead of falsely claiming `Renamed: A -> B`; `tx rm --revert` prints `Reverted.` and `rm --if-exists` on a missing object prints `Not found: <path> (nothing removed)` instead of exiting silently. JSON output gains an optional `reverted` field on both.
- Invalid `--serialization` values on `set`/`mv`/`rm`/`replace`/`save`/`init`/`script`/`bpa`/`macro` are rejected at parse time, matching `add`. Help text no longer advertises the unimplemented `te-folder`/`pbip`/`database.json` formats (`init` genuinely supports `pbip`).

### Changed

- `refresh` promoted from compatibility stub to real command.
- `TomServerModelSession` implements `IModelRefreshSession`.
- `tx add`/`set`/`rm`/`mv` help examples now use the canonical keyword-path form (`tables/Sales/measures/Revenue`) so they are copy-pasteable.
- `--save` to the source model (in-place) no longer errors with "Output directory already exists". In-place saves overwrite cleanly; `--save-to <existing>` still errors unless `--force` (now mapped to `TOMIX_SAVE_OUTPUT_EXISTS`).

### Fixed

- `--save` on an existing model directory no longer fails; the directory is cleared and rewritten so deleted objects don't leave orphan files.
- Empty `--type` on `tx add` now produces an actionable error ("No object type given…") instead of `Adding object type '' is not supported yet.`

### Added

- `tx refresh` triggers a data refresh on a deployed model with Tabular Editor-compatible flags
  (`--type`, `--table`, `--partition`, `--apply-refresh-policy` / `--skip-refresh-policy`,
  `--effective-date`, `--max-parallelism`, `--dry-run`, `--no-progress`, `--trace`). Targets the
  active remote connection by default, or the remote workspace-mode secondary when the default is
  local. Live per-table row counts during refresh are streamed from the XMLA `SessionTrace` into a
  Spectre `Live` table, and a final summary table reports per-table `Rows`, `Query`, `Read`, `Total`,
  and `Rows/s` plus a roll-up. `--output-format json|csv` machine output is supported.
- New `TOMIX_REFRESH_*` diagnostic codes (`TOMIX_REFRESH_NO_REMOTE_TARGET`, `TOMIX_REFRESH_UNSUPPORTED`,
  `TOMIX_REFRESH_BAD_TYPE`, `TOMIX_REFRESH_TABLE_PARTITION_CONFLICT`, `TOMIX_REFRESH_BAD_PARTITION`,
  `TOMIX_REFRESH_FAILED`).
- `Styling.Number(long)` and `Styling.DurationSeconds(double)` helpers for human-only output.

### Changed

- `refresh` promoted from a compatibility stub to a real command.
- `TomServerModelSession` now implements `IModelRefreshSession`; refresh is only supported on
  sessions connected to a live XMLA endpoint.

### Removed

- `tx info` command (use `tx load` or `tx connect` instead).

### Fixed

- `connect` accepts documented bare workspace names (e.g. `tx connect MyWorkspace Sales`) instead of rejecting them with `TOMIX_CONNECT_INVALID_TARGET`; the name is normalized to a fully-qualified `powerbi://` endpoint so every later command can open it.
- `refresh --partition` malformed values are routed through `ErrorOutput` as `TOMIX_REFRESH_BAD_PARTITION`, honoring `--error-format json` instead of writing raw text to stderr.
- `refresh` per-table `Query`/`Read`/`Total` accuracy — trace sink now maps `ExecuteSql` → Query, `ReadData` → Read + row count instead of wrong subclasses.
- `refresh` row counts captured in-flight via `ReadData.IntegerData`; removed broken post-refresh DMV query.
- `refresh --trace` bare flag now resolves to stderr as documented, instead of silently doing nothing.
- `connect --workspace` no longer drops primary connection when overwrite prompt is declined; exits 0 instead of 1.
- `connect --workspace` shows spinner during remote probe (no silent gap).
- `connect` rejects invalid targets (e.g. typos) with `TOMIX_CONNECT_INVALID_TARGET`.
- `--save-to` on mutation commands honors `--force` (no silent overwrite).
- Source resolution honors global `--server`/`--database` on `ls`, `get`, `find`, etc.
- `deploy --fix-bpa` blocks on remaining error-severity violations; unsupported sessions fail with `TOMIX_DEPLOY_FIX_UNSUPPORTED`.
- `refresh --trace` no longer disposes `Console.Error` — wrapped in `NonDisposingTextWriter`.
- `refresh` honors injected connection session in `ActiveModelResolver` (resolution + tests no longer require live remote session).
