# Tomix.Provider.Vpax

VertiPaq storage-statistics provider. Implements `Tomix.Core.Vertipaq.IVertipaqAnalyzer` on top
of the MIT-licensed sql-bi VertiPaq-Analyzer libraries (`Dax.Model.Extractor`, `Dax.ViewModel`,
`Dax.Vpax`, `Dax.Vpax.Obfuscator`).

## Responsibilities

- Extract statistics from a live engine source (`powerbi://`, `asazure://`, `localhost:<port>`)
  via `TomExtractor` (TOM metadata + ADOMD DMV queries).
- Read and write `.vpax` packages (`VpaxTools`), including obfuscated exports plus their
  `.dict` obfuscation dictionary.
- Map library view-model types to the `Tomix.Core.Vertipaq` records in `VpaStatsMapper`
  (percentages convert from 0-1 fractions to 0-100 there).

## Rules

- All `Dax.*`, ADOMD, and TOM types stay inside this project; public APIs return
  `Tomix.Core` types only.
- This is a standalone service, not an `IModelSession` capability: extraction opens its own
  connection from a connection string, and `--import` needs no model at all.
- Endpoint normalization and token handling mirror `TomServerModelProvider`
  (`TomModelDeployer.ResolveEndpoint`, Core `AccessToken` -> AMO `AccessToken`).
- Failures surface as `VertipaqAnalysisException` with a `VertipaqAnalysisKind`
  (never raw library exceptions) or `AuthenticationRequiredException`.

## Tests

- `tests/Tomix.Provider.Vpax.Tests` — offline: builds `Dax.Metadata.Model` instances in code,
  round-trips them through `ExportVpax`/`ImportAsync`, and unit-tests the mapper. No live
  engine, no fixtures on disk.

## Cross-folder dependencies

- Depends on `Tomix.Core` (contract + DTOs) and `Tomix.Provider.Tom` (endpoint resolution).
- Consumed by `Tomix.Cli` (composition root) and, via `IVertipaqAnalyzer`, by `Tomix.App`.
- Never referenced by `Tomix.Core` or `Tomix.App` directly.
