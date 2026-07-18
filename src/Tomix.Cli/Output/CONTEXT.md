# Output/

Shared output wiring for all commands.

## Responsibilities

- Format validation and dispatch (JSON, CSV, text).
- JSON serialization contract.
- CSV serialization.
- Error/diagnostic rendering to stderr.
- Rich text rendering (Spectre.Console tables, markup, colors).
- Color palette and styling helpers.

## Structure

- `OutputFormats` — canonical `--format` option, allowed values, and format predicates.
- `CommandOutput` — format validation, human/JSON/CSV dispatch, diagnostic printing, exit-code mapping.
- `JsonOutput` — single JSON serializer (the `--format json` contract).
- `CsvOutput` — CSV serialization (the `--format csv` contract).
- `PropertyCsvRenderer` — CSV columns/rows driven by the shared property catalog (`Tomix.Core.Properties.ModelPropertyCatalog`); `get` and `ls` both render CSV through it so their columns cannot drift.
- `ErrorOutput` — diagnostic rendering to stderr (JSON or colored text).
- `DidYouMean` — Levenshtein-based "Did you mean?" suggestion helper for unknown subcommands.
- `Spinner` — Spectre.Console Status spinner wrapper with auto-suppression (piped stdout, JSON/CSV, --quiet).
- `TraceWriter` / `NonDisposingTextWriter` — shared `--trace` destination plumbing for `refresh` and `query`: resolves the option value (bare/`-` → stderr, otherwise file) and opens the writer; the wrapper keeps `using` scopes from disposing the process-shared `Console.Error`.
- `LsRenderer` — Spectre.Console tables for the `ls` command.
- `QueryResultRenderer` — query rowset rendering for the `query` command (dynamic-column table, CSV, `-o` json/csv file output, stderr footer, and the `--trace`/`--plan`/`--runs` server-timings, query-plan, and benchmark summaries written to stderr).
- `VertipaqView` / `VertipaqRenderer` — pure layout logic and Spectre rendering for the `vertipaq` command.
- `CiAnnotations` — shared `--ci github`/`--ci vsts` logging-command syntax; callers project results into `CiAnnotation`s.
- `BpaRunView` / `BpaRunRenderer` — pure grouping/ordering logic, Spectre rendering, JSON projection, and CI annotation emission for `bpa run`.
- `ValidateRenderer` — CI annotation emission for `validate` (error-level only; issues carry no severity).
- `BpaRulesRenderer` — Spectre rendering and JSON projections for the `bpa rules` subcommands.
- `ConnectRenderer` — connected-model summary (text + JSON projection), show-current and raw-connection views for the `connect` command.
- `RefreshRenderer` / `RefreshLiveDisplay` — `refresh` command rendering: per-table statistics (text + CSV), `--dry-run` TMSL pretty-print, and the live `AnsiConsole.Status()` progress display fed by XMLA trace events.
- `IncrementalRefreshRenderer` — text rendering for the `incremental-refresh` subcommands (`show`, `set`, `rm`, `apply`).
- `Styling` — color palette, markup helpers, and shared utilities. The single source of truth for all color/style decisions.

## Color Strategy

See [`/docs/cli-color-strategy.md`](../../docs/cli-color-strategy.md) for the full palette, message categories, and styling rules.

Key rules:

- Use `Styling` helpers and `Palette` constants. Do not hard-code Spectre markup strings or raw ANSI escape codes.
- Tables use `Styling.NewTable()` (rounded border, Slate border color).
- All user-facing text must go through `Styling.MarkupEscape()` to prevent bracket injection.
- JSON, CSV, TMDL, BIM, and CI annotation output paths must never contain markup.
- `noColor` config and `NO_COLOR` env var disable color via `AnsiConsole.Profile.Capabilities.ColorSystem`.

## Cross-folder dependencies

- Used by every command in `Commands/` via `CommandOutput.Render(...)`.
- Depends on `Tomix.Core` for result types, diagnostics, and configuration.
- Depends on `Spectre.Console` — must stay within `Tomix.Cli`, never leak to App or Core.

## Rules

- Do not hand-roll JSON output; serialize through `JsonOutput`.
- Do not add Spectre.Console usages outside this directory and `Commands/`.
- Do not reference provider-specific types.
- Command-specific renderers live here as `<Command>Renderer` (plus an optional Spectre-free `<Command>View` for unit-testable layout logic); commands themselves must not contain Spectre table/grid rendering or JSON projections.
- When adding new styling, extend `Styling.cs` — do not create per-command color constants.
