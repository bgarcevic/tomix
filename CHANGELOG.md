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
