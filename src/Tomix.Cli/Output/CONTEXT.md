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
- `SyntaxErrorCaret` — the source line and `^` marker under an expression syntax error, for inline `tx format -e` failures in text mode.
- `StdOut` — the redirected-stdout policy applied at startup: unwrapped instead of Spectre's 80-column fallback, and Unicode off so `Styling.Border`/`Styling.TreeGuide` pick ASCII.
- `StdErr` — the stderr console for commentary (banners, hints, prompts, notices). Use it instead of constructing `AnsiConsole.Create(... Console.Error)`; it is created per call so tests can swap `Console.Error`, and it inherits the no-color setting. Stdout carries only results.
- `DidYouMean` — Levenshtein-based "Did you mean?" suggestion helper for unknown subcommands.
- `HelpRenderer` (`SpectreHelpAction`) — all `--help` output: root sections, per-command notes and
  examples, wrapping to the terminal width. `HelpGroups` tags options into named help sections;
  `HelpPlaceholders` names each option's value (`<path>`). Layout and text rules are enforced by
  `HelpLayoutTests` (see the Help section of `docs/cli-ux-guidelines.md`).
- `Spinner` — Spectre.Console Status spinner wrapper with auto-suppression (piped stdout, JSON/CSV, --quiet).
- `TraceWriter` / `NonDisposingTextWriter` — shared `--trace` destination plumbing for `refresh` and `query`: resolves the option value (bare/`-` → stderr, otherwise file) and opens the writer; the wrapper keeps `using` scopes from disposing the process-shared `Console.Error`.
- `LsRenderer` — Spectre.Console tables for the `ls` command.
- `QueryResultRenderer` — query rowset rendering for the `query` command (dynamic-column table, CSV, `-o` json/csv file output, stderr footer, and the `--trace`/`--runs` server-timings and benchmark summaries written to stderr).
- `GetView` — pure layout for the `get` text view: authored properties, annotation/translation sections, and the folded `Not set:` list of writable defaults (`PropertyDescriptor.IsDefault`); `--all` lists everything.
- `SummaryRenderer` — the `summary` text view, laid out like `get` (muted aligned keys, a `Contents` count section) with a stderr next-step hint.
- `GetRenderer`, `DepsRenderer`, `DeployRenderer`, and `ValidateRenderer` —
  complex command-specific text/table rendering and machine-output projections.
- `VertipaqView` / `VertipaqRenderer` — pure layout logic and Spectre rendering for the `vertipaq` command.
- `CiAnnotations` — shared `--ci github`/`--ci vsts` logging-command syntax; callers project results into `CiAnnotation`s.
- `TrxWriter` — shared `--trx` VSTEST file writer for `validate` and `bpa run`; callers project results into `TrxWriter.TrxTest`s (projections live on `ValidateRenderer`/`BpaRunRenderer`).
- `BpaRunView` / `BpaRunRenderer` — pure grouping/ordering logic, Spectre rendering, JSON projection, and CI annotation emission for `bpa run`.
- `ValidateRenderer` — CI annotation emission for `validate` (error-level only; issues carry no severity).
- `BpaRulesRenderer` — Spectre rendering and JSON projections for the `bpa rules` subcommands.
- `ConnectRenderer` — connected-model summary (text + JSON projection), show-current and raw-connection views for the `connect` command.
- `RefreshRenderer` / `RefreshLiveDisplay` — `refresh` command rendering: per-table statistics (text + CSV), preview TMSL pretty-print, per-partition rows and the phase table, and the live `AnsiConsole.Live()` progress panel fed by XMLA trace events (trace thread updates state under a lock; a render loop on the Live context redraws it).
- `UpdateRenderer` — `update` command rendering: `--check` release-notes preview with `[breaking]` badges, and the performed-update summary line.
- `Styling` — color palette, markup helpers, and shared utilities. The single source of truth for all color/style decisions; `ExpressionMarkup`/`DaxMarkup`/`MMarkup` are the shared DAX and M highlighting path.

## Color Strategy

See [`/docs/cli-color-strategy.md`](../../docs/cli-color-strategy.md) for the full palette, message categories, and styling rules.

Key rules:

- Use `Styling` helpers and `Palette` constants. Do not hard-code Spectre markup strings or raw ANSI escape codes.
- Tables use `Styling.NewTable()` (rounded border, Slate border color; ASCII when redirected). A table or tree built directly takes `Styling.Border` / `Styling.TreeGuide`.
- Escape model-derived text exactly once at a Spectre markup boundary. Use `Styling.MarkupEscape()`
  when inserting raw text into markup; pass raw text to `Styling` helpers, which already escape it.
  Literal `WriteLine` and JSON/CSV/TMDL/BIM output do not use Spectre escaping.
- Expression text goes through `Styling.ExpressionMarkup` with an `ExpressionLanguage`: DAX when `DaxExpressions.IsDaxValue`/`IsDaxExpression` says so, M when `MExpressions.IsMValue`/`IsMExpression` (Tomix.App.M) says so, plain otherwise — so every renderer classifies and highlights expressions the same way.
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
- Command-specific renderers live here as `<Command>Renderer` (plus an optional Spectre-free
  `<Command>View` for unit-testable layout logic). Commands may own prompts and trivial one-line
  interaction, but must not contain tables, trees, multi-format serialization, or JSON projections.
- When adding new styling, extend `Styling.cs` — do not create per-command color constants.
