# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

See [docs/cli-ux-guidelines.md](docs/cli-ux-guidelines.md) for the versioning policy
and the API surface that major versions protect.

## [Unreleased]

- Remote model support: `connect`, mutate (`add`/`set`/`rm`/`mv`/`replace`), and `deploy` over XMLA
  (Power BI / Fabric / Analysis Services), with local workspace mirroring, staging, and revert.
- BPA engine rewritten to evaluate all 70 bundled rules generically via Dynamic-LINQ, with structured
  diagnostics (errors/disabled/ignored), per-model and per-user ignore/disable, and external rule collections.
- Project renamed `mdl-cli` → `tomix-cli` and command `tomix` → `tx`; namespaces, env vars, config dir,
  and `TOMIX_*` diagnostic codes updated accordingly. MinVer-based versioning and CI/release automation added.

### Removed

- `tx info` command: dropped the standalone exploration entry. Model summaries remain available via
  `tx load` and `tx connect`, which reuse the same summary handler internally.

### Fixed

- `--save-to` on mutation commands now honors the absence of `--force`: the TOM and TMDL sessions
  previously forwarded `Force: true` to the exporter, bypassing `TomModelExporter`'s overwrite guard
  and silently overwriting existing BIM/TMDL targets. They now forward the user's `--force` choice.
- Source model resolution now honors the recursive global `--server` (with `--database`) option.
  Commands like `ls`, `get`, `find`, and other source-resolving commands previously ignored
  `--server`, so `tx ls --server powerbi://... --database Model` resolved to an empty model and
  failed with `TOMIX_NO_PROVIDER` instead of opening the requested remote model.
- `deploy --fix-bpa` no longer proceeds when error-severity BPA violations remain after auto-fix.
  The gate's fail condition was previously suppressed whenever `--fix-bpa` was set, so deploys
  went through with known violations. The engine is now re-evaluated after fixes are applied and
  the deploy is blocked if any error-severity violation is still present; use `--skip-bpa` to
  bypass. `deploy --fix-bpa` on a provider whose session cannot apply fixes now fails clearly with
  `TOMIX_DEPLOY_FIX_UNSUPPORTED` instead of silently skipping the fixes and proceeding.
