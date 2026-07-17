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
- `LsRenderer` — Spectre.Console tables for the `ls` command.
- `QueryResultRenderer` — query rowset rendering for the `query` command (dynamic-column table, CSV, `-o` json/csv file output, stderr footer).
- `Styling` — color palette, markup helpers, and shared utilities. The single source of truth for all color/style decisions.

## Color Strategy

See [`/docs/cli-color-strategy.md`](../../docs/cli-color-strategy.md) for the full palette, message categories, and styling rules.
See [`/docs/spectre-migration.md`](../../docs/spectre-migration.md) for the migration plan and per-command progress.

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
- Keep `LsRenderer` and `QueryResultRenderer` as the only command-specific renderers factored into separate files.
- When adding new styling, extend `Styling.cs` — do not create per-command color constants.
