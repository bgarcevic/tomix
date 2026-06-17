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
