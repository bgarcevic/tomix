# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

See [docs/cli-ux-guidelines.md](docs/cli-ux-guidelines.md) for the versioning policy
and the API surface that major versions protect.

## [Unreleased]

### Added

- `tx bpa rules add`, `set`, `remove`, and `init` author custom BPA rules from the CLI. They edit
  your config-dir `bpa-rules.json` (which `bpa run` loads) or the file `--rules-file` names, and
  keep fields tx does not model. Scope and severity are validated (#232).
- `tx bpa run --fix` now fixes `DAX_COLUMNS_FULLY_QUALIFIED` and `DAX_MEASURES_UNQUALIFIED`
  by rewriting the object's DAX, through two new fix expressions:
  `QualifyColumnReferences()` and `UnqualifyMeasureReferences()`. References that cannot be
  resolved for certain (a column name in several tables, a name that is also a string literal,
  a measure qualified with the wrong table) are reported as fix errors and left unchanged (#267).
- `tx doctor` reports the current session's connection (`current-session`) and, on Windows,
  warns when you are signed in but the token cache file is missing.
- `tx doctor --show-details` includes account, server, database, model, profile, and session
  names and full paths. Without it, the report is safe to paste into a bug report: the home
  directory is shown as `~` and names are replaced by what kind of thing they are.
- `tx doctor` suggests `tx session prune` when session files of exited shells are left over.

### Changed

- `tx doctor` output is shorter and only warns when something needs doing. Optional states
  (no profiles, not signed in, no update check yet) are reported with the new `Info` status
  (`INFO`) instead of `WARN`. The `runtime`, `operating-system`, `terminal`, and
  `model-providers` checks are removed because they could never fail; the runtime, OS, and
  terminal details are still in the header and in the JSON fields. The `update-cache` message
  now shows a short UTC time and says to run `tx update`. A source build (`./tx`) or an install
  `tx update` cannot update reports a newer release as `INFO` without that hint, matching the
  update notice. The header and JSON (`installKind`) show how tx was installed.
- `tx doctor` no longer prints the end-of-command "a new version is available" notice (its
  `update-cache` check already says so) and no longer refreshes the update cache over the
  network, keeping its promise to stay local.
- `tx doctor` no longer creates `~/.tomix` on a machine where tx has not run yet; it reports
  that the directory will be created on first use.

### Changed

- `tx bpa rules list` shows each rule's scope, and lists the rules in your config-dir
  `bpa-rules.json` (source `user`), matching what `bpa run` loads (#232).
- Config, profiles, recent connections, session, staging, update-check, auth, BPA, and
  test-snapshot files are now read and written with source-generated JSON instead of runtime
  reflection, as groundwork for a faster-starting Native AOT build. The files' format is
  unchanged, so existing ones keep working.
- Release binaries and the `dotnet tool` package no longer include `msalruntime` and
  `msasxpress`, two native libraries tx never uses for sign-in (the release binary is about
  3 MB smaller). The `dotnet tool` package now talks to XMLA endpoints uncompressed, as the
  release binaries already did.

## [0.6.0] - 2026-09-29

### Fixed

- `tx validate` no longer reports every column reference into a calculated table as missing
  (`DAX0002`) when its TMDL declares no columns because the table was never evaluated. Columns
  are inferred from `DATATABLE(...)` and `ROW(...)` expressions, so typos are still caught;
  references into calculated tables whose columns cannot be inferred offline are not judged (#320).
- `tx validate` in CI logs: with `--ci` (or under Azure Pipelines), findings are no longer
  hard-wrapped at 80 columns, so each one stays on its own line. In Azure Pipelines, a column
  named like a log command (`[Group]`, `[Section]`, `[Error]`, ...) no longer turns its line
  into a collapsible section header that drops the `##[error]` styling and leaves stray `8m`
  fragments in the table.
- `tx validate` no longer warns (`DAX0003`) about columns an expression defines for itself:
  `[@Name]` when `"@Name"` is a new column's name in `ADDCOLUMNS`, `SELECTCOLUMNS`,
  `SUMMARIZE`, `SUMMARIZECOLUMNS`, `GROUPBY`, `ROW`, or `DATATABLE`, and `[Value]` from
  `GENERATESERIES` or a `{ ... }` table constructor (not an `IN { ... }` list).
- `tx validate` now checks where those columns are used: a column an expression builds is
  accepted only where a row of its table is in context (inside `FILTER`, `SUMX`, and other
  iterators over it, also through a `VAR`), and a use outside it warns with `DAX0003` "is built
  by this expression but used outside the table that has it".

## [0.5.0] - 2026-09-29

### Added

- `tx bpa run --fix --dry-run` previews fixes without changing the model. Each pending fix is
  listed as `Would fix:` (or `Would delete:` with `--allow-delete`) with the property's value
  before and after, followed by how many findings would remain. Nothing is applied, saved, or
  staged, and the exit code still follows `--fail-on` for the model as it is. JSON adds
  `dryRun`, `fixesPending`, `wouldRemain`, and a `fixes` array with each fix's before and after
  values; outside a dry run, `fixes` lists the fixes that were applied (#268).
- `tx connect <workspace> --list` lists the semantic models on a workspace or XMLA
  endpoint without connecting (name, compatibility level, last update). It works
  non-interactively and with `--output-format json`, so scripts and agents can
  discover models on a workspace that hosts more than one.
- `tx refresh` reports refresh progress in detail: a live panel shows overall progress and each
  in-progress table's step (querying, reading, compressing, hierarchies, calculated columns),
  partition, rows, and running time, then the model-level steps (relationships, calculation
  script, commit). It replaces the single status line, which on large models was cut off at the
  terminal width and kept showing the same few tables. The summary adds a `Process`
  column, a row per partition for multi-partition tables, and a phase table; JSON gains
  `tables[].processMs`, `tables[].partitions`, and `phases`.

### Changed

- Opening a workspace that hosts several models without naming one now fails with
  `TOMIX_DATABASE_REQUIRED` (exit 2) and a hint to run `tx connect <workspace> --list`,
  instead of `TOMIX_CONNECT_FAILED` / `TOMIX_QUERY_FAILED` (exit 1).

### Removed

- **Breaking:** `tx query --plan` and the `plans` field of `tx query --output-format json`. Power BI /
  Fabric XMLA endpoints never deliver query-plan trace events, so the option could not return a plan.

### Fixed

- `tx refresh` reported only the last partition's rows and timings for tables with several
  partitions, and on Power BI / Fabric could summarize before the trace arrived, reporting zeros
  (typically with `--output-format json`).
- `tx query --trace` returns server timings on Power BI / Fabric XMLA endpoints. The trace was
  rejected for an unsupported column, and events that arrived were then silently discarded; timings
  (and `benchmark` storage-engine stats) came back `null`. A missing timing now always comes with a
  warning on stderr.

## [0.4.3] - 2026-09-28

### Changed

- Release archives contain only the `tx` executable. The native libraries it needs
  (`msalruntime.dll`, `msasxpress.dll`) are embedded and unpacked to the .NET bundle
  cache on first run instead of shipping next to `tx.exe`.

## [0.4.2] - 2026-09-28

### Changed

- Help is reorganized and wraps to the terminal. It wraps at the terminal width (up to
  100 columns) with descriptions indented under their column, instead of letting the
  terminal break lines mid-word. Root help lists commands first. A command's page
  names the global options on one line instead of repeating all of them, groups long
  option lists (`Save options:`, `Rule options:`, `Compatibility options:`, ...), and
  shows short value names (`--save-to <path>`). The help option is listed as
  `-h, --help`; `-?`, `/?`, and `/h` still work.
- `tx help <command>` shows a command's help, the same as `tx <command> --help`.
  Bare `tx` shows just the command list.
- Command descriptions are now short one-liners; details such as `diff`'s exit codes
  moved to the command's own help page.

### Fixed

- A mistyped command (`tx lss`) prints one error with a suggestion on stderr and exits
  `2`, instead of three messages in the wrong order followed by the full help on stdout.
  It carries the new code `TOMIX_UNKNOWN_COMMAND`; other parse errors (a missing
  argument or option value) carry `TOMIX_USAGE` and also no longer print the help page.
- A command group run without a subcommand (`tx bpa`, `tx config`) shows its help and
  exits `0` instead of failing with "Required command was not provided."
- The unknown-option hint names the full command, e.g. `tx bpa run --help` rather
  than `tx run --help`.
- For a model file, the `bpa run` hint about collecting VertiPaq statistics now
  lists the two commands on separate lines. It used to be one long sentence that
  wrapped in a normal-width terminal.

## [0.4.1] - 2026-09-28

### Fixed

- `bpa run --fix` evaluates the rules again after fixing, and the exit code and
  `--fail-on` now apply to the findings that remain. A run that fixes every blocking
  finding exits `0`. JSON adds a `remaining` count (#297).
- `bpa run --output-format json` includes `objectPath` and `description` for each
  result, so a finding can be addressed from a script (#258).
- A failed `--stage` mutation no longer leaves an empty staged working copy behind.
  Earlier staged work is kept (#289).

## [0.4.0] - 2026-09-28

### Added

- `bpa rules show <rule-id>` prints one rule in full: its description and reference
  link, source, status, scope, expression, and fix expression (`--output-format json`
  supported). An unknown ID fails with `TOMIX_BPA_RULE_NOT_FOUND` and suggests IDs that
  contain what you typed.
- `bpa rules disable` and `bpa rules ignore` reject a rule ID that no known rule has,
  so a typo no longer silently disables nothing. The check covers the bundled catalog,
  the config-dir `bpa-rules.json`, and (for `ignore`) the model's own rules. It is
  skipped when a rule source can't be read, and `--allow-unknown` bypasses it. `enable`
  and `unignore` still accept any ID.
- Power Query (M) expressions are syntax-highlighted in text output, like DAX: `get`
  properties, `ls` partition and shared-expression cells, and `format --lang m` output.
  Keywords, library functions, step and field definitions, field access, literals, and
  comments take the same palette roles as their DAX counterparts. JSON/CSV output and
  piped or redirected text stay plain (#285).
- `tx connect --local --list` lists running Power BI Desktop instances (report name,
  `localhost:<port>` endpoint, database id) without connecting, so scripts and agents can
  choose one without the picker. JSON via `--output-format json` (#299).
- `set` accepts `translation:<culture>/<property>` assignments, where the property is
  `caption` (alias `name`), `description`, or `displayFolder`, on tables, columns,
  measures, hierarchies, levels, and the model root (`.`). An empty value removes the
  translation. A missing culture fails and tells you to add it; it is never created
  implicitly. `get` lists translations after annotations under the same keys, in text
  and JSON, and `--query translation:da-DK/caption` finds them (#227).
- DAX syntax checks now use the parser, so grammar errors that used to pass are reported
  with their position: `validate` reports `DAX0007` (unexpected token), `DAX0008`
  (missing expression), and `DAX0009` (malformed `VAR` block), and `format -e` for DAX
  shows the same structured `syntaxErrors` and caret as M. Only the first grammar error
  is reported. Queries and `Name := expr` scripts are not flagged for trailing text
  (#203).

### Changed

- `bpa run` text output is redesigned. Findings are grouped by severity, one block per
  rule, and rule IDs are never split across lines. Each rule shows its category and
  whether its fixes can be applied (`fixable`). A single summary line counts the
  findings and the rules that passed. Next-step commands are printed ready to copy on
  stderr, reusing your model path and rule options. After `--fix`, the output shows how
  many findings were fixed and remain, and whether the result was saved, staged, or kept
  in memory only. JSON, TRX, and CI output are unchanged.
- `bpa rules list` text output replaces the truncating five-column table. Rules are
  grouped by category, and each rule shows its name, ID, severity, status, and whether it
  is `fixable`, with nothing cut off. Source is shown only when the listing mixes
  sources.
- **Breaking:** `bpa rules list` now tells ignored rules apart from disabled ones. A rule
  on the model's ignore list has `status: "ignored"` (it was `"disabled"`), and
  `summary.ignored` is counted (it was always `0`). `--ignored` and `--disabled` now
  filter separately; before, both returned the same set.
- **Breaking:** mutation JSON results share one persistence contract (#161). `saved` is
  always a bool; the path or `server / database` moved to `savedTo`. A new `status`
  (`saved`, `staged`, `preview`, `dryRun`, `unchanged`, `reverted`) replaces `staged` and
  `reverted`. `synced`/`syncTarget`/`syncWarning` became a `sync` object whose `status` is
  `notAttempted`, `notConfigured`, `skipped`, `succeeded`, or `failed`. `dryRun` is always
  emitted. Previews and dry runs report the object as `wouldAdd`/`wouldMove`/`wouldRemove`/`wouldSet`
  instead of `added`/`moved`/`removed`/`set`. New `persistence` (`file`, `liveModel`,
  `service`) and `target` (`server`, `database`, friendly `model`) fields say where a save
  landed. `format` reports its per-object result as `formatStatus`. A save to Power BI
  Desktop prints a reminder on stderr to save the report in Desktop. See
  [JSON result](docs/commands/modify.md#json-result).
- TMDL saves rewrite only the files whose content changed, so a small edit gives a
  small git diff. Untouched files keep their bytes, line endings, and M partition
  `source =` indentation, and a failed save leaves the model intact. Only stale
  `.tmdl` files are deleted; a README or other non-TMDL file in the model folder is
  no longer removed on save (#224, #201).
- **Breaking:** mutation saves and staged commits now block newly introduced validation
  errors before writing. Existing errors and warnings remain non-blocking; `--force` saves
  with a notice, and `tx config set validateOnSave false` disables the gate. `validate`
  also reports direct DAX self-references as `DAX0006` (#210).
- **Breaking:** removed `incremental-refresh`. Inspect, configure, and remove policies
  with `get`/`set`/`rm <table>/RefreshPolicy`. `set` accepts repeatable `-p`/`--set`
  assignments. Use `refresh --table <table>` to apply the deployed policy and load
  data, or `refresh --table <table> --policy-only` to manage partitions without
  loading data. The policy-only mode supports `--dry-run` and effective dates.
  See the [migration guide](docs/commands/modify.md#migration-from-incremental-refresh).
- `format --lang m` formats Power Query (M) offline with Microsoft's powerquery-formatter,
  bundled into `tx` and run in-process, instead of calling the powerqueryformatter.com API:
  no network, no Node.js, air-gap safe. The layout can differ from the API's, so the first
  `format --lang m` run over a model formatted with a previous release may rewrite
  partitions. M that does not parse is left unchanged and reported with its line and column
  (#196).
- **Breaking:** `format` syntax errors are structured (#197). `TOMIX_FORMAT_FAILED` errors
  gain `line`, `column`, `objectPath`, and `syntaxErrors` (`stage`, `code`, start and end
  positions), and inline `-e` failures show the source line with a caret in text mode. In a
  whole-model run, each failed row's `error` is now an object (`message` plus the optional
  position fields) instead of a string, and any failure exits `1` with
  `TOMIX_FORMAT_FAILED` and applies nothing (it used to exit `0`). `--path` failures report
  `TOMIX_FORMAT_FAILED` instead of `TOMIX_MUTATION_FAILED`. `InlineFormatResult` drops its
  always-empty `errors` field. DAX errors now say `on line X, column Y:` like M.
- **Breaking:** without `TOMIX_SESSION`, the active connection is scoped to the enclosing
  git repository or worktree root (else the current folder) instead of one global
  `default` session, so a connection no longer follows you into another repo. Subfolders
  share their repo's session; `TOMIX_SESSION` still names a session explicitly. The old
  `default` session is no longer read: run `tx connect` once per repository, and
  `tx session prune --all` removes the orphaned `default.json`. `tx session` shows
  `kind: directory` and its `scope` (#307).
- `tx connect` marks a Power BI Desktop session whose report has closed as `(not running)`,
  names the report, and suggests `tx connect --local` or `--clear`; JSON adds `reachable`
  for Desktop sessions. The `Connected to:` banner names the Desktop report, warns when it
  has closed, and is now also shown for `save` and `vertipaq` (#307).
- The `standard` BPA ruleset (the `bpa run` default and the deploy gate) is re-curated so
  error severity means a broken model. Six style and convention rules drop from error to
  warning, so they no longer block `deploy`. Five noisy or heuristic rules move to `full`,
  and four precise ones join `standard` (26 rules). False positives are fixed for
  calculation-group columns, undeclared column types, field-parameter tables, and
  `USERELATIONSHIP` with reversed arguments. See [BPA rulesets](docs/commands/validate.md).

### Removed

- The `TOMIX_POWERQUERY_FORMATTER_API` environment variable. M formatting no longer uses a
  network endpoint, and there is no network fallback (#196).

### Fixed

- BPA rules that read VertiPaq statistics no longer pass silently on a model that has
  none. A missing statistic read as 0, so the high-cardinality relationship,
  large-table partitioning, and referential-integrity rules reported clean without
  checking anything. `bpa run` now lists them as "Not checked", keeps them out of the
  passed count, and reports them in JSON as `missingVertipaqStatsRules`. `deploy`
  proceeds with a `TOMIX_BPA_VERTIPAQ_STATS_MISSING` warning that names them. Both say
  how to collect the statistics: `tx vertipaq --annotate --save` on a deployed model, or
  on a local model connected to one in workspace mode (#266).
- `tx connect --local` and `tx connect localhost:<port>` now look up and save the Desktop
  instance's database (a GUID) and open the model to validate it, so `tx vertipaq --export`
  works without `-d`. VertiPaq also resolves the database when an older saved session has none (#299).
- Stderr commentary (banners, hints) is no longer hard-wrapped at 80 columns when stderr
  is redirected, so `2> log.txt` keeps a long model path on one line.
- Renames rewrite DAX references in user-defined functions (UDFs) and to UDFs and
  calendars (#228). A UDF body that references a renamed measure, column, or table is
  rewritten instead of reported as broken. Renaming a UDF rewrites its call sites
  (`AddTax(...)`, `Local.AddTax(...)`) and leaves built-in functions, strings, and comments
  alone. Renaming a calendar rewrites `'Fiscal'` references. When a table and a calendar
  share the renamed name, `'Fiscal'` could mean either, so — whichever one is renamed —
  those references are reported in `brokenReferences` (and fail `--strict-refs`) rather
  than guessed. `rm` now blocks removing a UDF or calendar that DAX still references, and
  removing a table checks references to its calendars. `deps` now shows UDF call edges,
  and `validate` no longer reports a quoted calendar reference as a missing table.
- Text-mode banners and hints go to stderr, so `tx validate > file` (and `bpa run`,
  `refresh`, `test`, `script`, `deploy`) captures only the result. Moved: the
  "Validating:", "BPA analysis ·", "Refreshed … on …", and "DAX tests ·" banners; "Try"
  and "Run …" hints; mutation "Dry run", "Not saved yet", and "Staged" notices; and
  workspace sync warnings. Result lines, tables, and counts stay on stdout (#255).
- Refresh scripts with `--effective-date` now explicitly include `applyRefreshPolicy`,
  as required by the XMLA endpoint. Verified with the inline refresh-policy QA sample.
- Usage errors give one suggestion, aimed at what was mistyped: `tx auth lgin` suggests
  `login` instead of repeating `auth`, an unknown option such as `--forse` suggests
  `--force` (`TOMIX_UNKNOWN_OPTION` in JSON), and model paths and extra arguments no
  longer get unrelated command guesses (#180).
- `--set name=` (an explicit empty value) no longer reads stdin, so clearing a property
  from a script or CI job with redirected stdin doesn't hang. Only `-` reads stdin.
- `tx session --output-format json` no longer includes the Desktop report's port-file
  path (#307).

## [0.3.0] - 2026-09-25

### Added

- `query` accepts DAX or DMV text as a positional argument. If query text is supplied through
  more than one of the positional argument, `--query`, and `--file`, it reports
  `TOMIX_QUERY_INPUT_CONFLICT`. A mistaken `-q <text>` is diagnosed as
  `TOMIX_QUIET_COLLISION`; `-q` is the global `--quiet` flag (#218).
- `validate` issues carry a severity: `--output-format json` includes a `"severity"` string on
  each issue, `--ci` annotations emit warnings as warnings (github `::warning::` / vsts
  `type=warning`) instead of dropping them, and the TRX projection maps each issue's severity
  to its test outcome. The run still exits `1` only for error-severity issues (#220).
- DAX syntax highlighting now extends beyond `get` and `ls`: `validate` shows the offending
  expression line under each finding, `set` previews DAX edits with `Before:`/`After:` lines,
  and `format` colors inline `-e` and `--path` output. Text output only — JSON/CSV, CI
  annotations, and TRX stay plain, and piped or redirected output degrades to plain text (#202).

### Changed

- `deploy` accepts `--bpa-fail-on error|warning` (default `error`) and applies that threshold
  consistently to both BPA gate phases — the pre-deploy check and the re-check after `--fix-bpa`
  fixes. The default path previously failed on any violation (info included) while the fix path
  failed only on error-severity violations, so warning-only findings could pass one phase and
  fail the other (#221). `bpa run --fail-on` shares the same threshold semantics.
- `set` reports the real post-mutation error count in `validationErrors` (measured with the
  same offline analysis `validate` runs) instead of a hardcoded `0`. `--revert` omits the field
  from JSON rather than reporting a count it never measured (#220).
- Command banners name the model through one resolution chain (TOM name → `.platform`
  displayName → folder name): the `deploy` banner shows the resolved display name instead of
  the raw source path, and `test`/`query` database names use the same fallback as `validate`
  (#220).
- Rewrote command help text and user-facing messages in the CLI's own voice across the command
  surface (`deploy`, `find`, `diff`, `add`, `bpa`, `connect`, `auth`, `config`, `deps`, `get`,
  `format`, `ls`, `profile`, `refresh`, `replace`, `rm`, `save`, `script`, `session`, `set`,
  `validate`, `vertipaq`, `incremental-refresh`, and the shared global/lifecycle options).
- `format` formats DAX offline with the bundled formatter engine instead of calling the
  daxformatter.com API: no network, no rate limits, air-gap safe. The output layout differs —
  a 65-column prettier-style layout with keywords and known function names upper-cased — so
  the first `format` run over a model formatted with a previous release rewrites every
  expression. Power Query (M) formatting still uses a network API, and `--long` now affects
  only M line width.
- `format` reports DAX syntax errors with their line numbers and refuses to reformat invalid
  expressions; when the offline formatter declines an expression, the message names the line
  of the first difference it would have introduced.
- `format` treats expressions that differ from the formatter's output only in line endings as
  unchanged (the formatter emits CRLF on Windows while TMDL stores LF), so repeat runs no
  longer re-report every multi-line expression as formatted.

### Fixed

- BPA rules that cannot be compiled or evaluated now appear as error-severity findings, so
  `bpa run`, `save`, and `deploy` fail their BPA gates instead of silently passing. The gate
  message names the broken rule and its error (#263).
- `deploy` honors disabled BPA rules in its gate, matching `bpa run` (#272).
- BPA's `IsAvailableInMDX` rules read the column's actual property value, avoiding false
  findings on hierarchy and sort-by columns and missed findings on hidden columns (#251).
- `ls` table column counts include calculated columns, and `get` no longer attempts model
  resolution for a path it cannot resolve (#262).
- Model and table names containing markup characters render literally in command output;
  `refresh` and `incremental-refresh apply` also keep their live status displays working
  with such table names (#223, #264).
- Power Query (M) formatting sends the content type accepted by the formatter service.
  Whole-model `format` runs now report each failed object's error in text and JSON output
  instead of only a failure count (#200).
- `deploy --profile` now fails with the profile name and a recovery hint when the profile is missing or has no server, before confirmation or model work (#179). Deploy and profile help now show how to create and use a profile.
- `diff` and `deploy --dry-run` no longer report engine-derived data type differences
  between calculated-table columns present on both sides of a live comparison. Other
  column property changes and offline source-to-source type differences remain visible (#177).
- `save --serialization bim`, `init --serialization bim`, and `deploy`'s TMSL script now write
  JSON with LF line endings on every OS. The indented writer's default newline was
  `Environment.NewLine`, so Windows produced CRLF and Linux LF — the same model saved on
  different machines differed byte-for-byte and fought over line endings in git. Collection
  order in the exported `.bim` remains TOM model order (tests now pin it) (#256).
- `rm --dry-run` previews truthfully in both directions. It now prints `Would remove: <path>`
  (instead of `Removed:`) and exits 0; when the reference guard would block the removal, the
  preview lists the dependents (`Would break N DAX reference(s) in: ...`) and hints `--force`
  instead of failing, so a dry run is how you discover that `--force` is needed. JSON output
  gains an additive `dryRun` field and the guarded preview reports `reason: "would_block"`.
  rm's `--dry-run` help text now matches the other mutation commands (#217).
- Calculated columns count as DAX hosts again. `deps` reports the upstream/downstream edges of
  calculated-column expressions (so `--unused` no longer flags a column that only a calculated
  column references), `validate` checks those expressions and no longer reports references to
  calculated columns or a `sortByColumn` bound to one as broken, and renames/removals rewrite
  or guard DAX inside calculated columns.
- Calculated columns are visible to the remaining column-scoped surfaces: `get` renders them in
  `--output-format tmdl` and `--output-format bim` fragments (`column X = <expr>` with an
  `expression` field) instead of silently dropping them from tables and ignoring the requested
  format for a calculated column, `bpa run` evaluates column rules against them, and `script`'s
  `Columns.Count` / `Columns[i]` now match TOM's `Table.Columns` (calculated columns included).
- `format --type calculatedcolumn` and `set` can write the `expression` property on calculated
  columns; the TOM provider rejected it as unsupported for columns.

### Removed

- Breaking change: `format --semicolons` and `format --no-space-after-function` were removed.
  They only fed the retired daxformatter.com API; the offline formatter has no equivalents.
- The `Dax.Formatter` NuGet dependency.

## [0.2.1] - 2026-09-10

### Fixed

- `update` no longer ends with a stale "A new version of tx is available" notice for the very
  update it just applied. The end-of-command notice compares the running process's version
  against the cached latest; a successful in-place update leaves that version old while the
  update check has just cached the new one, so the notice announced the update that had just
  happened and invited a pointless re-run. `tx update` (including `update --check`) now never
  triggers the throttled notice — the command reports its own result (#191).

## [0.2.0] - 2026-09-09

### Added

- `set` on tables now accepts every writable scalar property TOM exposes, in addition to the
  existing `name`, `description`, `isHidden`, and `dataCategory`: `isPrivate`,
  `excludeFromModelRefresh`, `excludeFromAutomaticAggregations`, `alternateSourcePrecedence`,
  `showAsVariationsOnly`, `systemManaged`, `directLakeIndexingBehavior`, `lineageTag`, and
  `sourceLineageTag`. `get`/`ls`/`find` JSON, CSV, and text output gain the matching read-side
  keys (additive — existing keys and their order are unchanged), and `diff` reports changes to
  the refresh/aggregation/DirectLake behavior properties (#114).
- `set` on measures and KPIs now covers the rest of their writable scalar surface: measures gain
  `dataCategory`, `isSimpleMeasure`, `sourceLineageTag`, and a writable `lineageTag`; KPIs gain
  `statusGraphic`, `trendGraphic`, `statusDescription`, `targetDescription`, and
  `trendDescription`. `get`/`ls`/`find` JSON, CSV, and text output gain the matching read-side
  keys (additive), and `diff` reports changes to them (#115).
- `set` on hierarchies and levels now covers the rest of their writable scalar surface:
  hierarchies gain `hideMembers`, `lineageTag`, and `sourceLineageTag`; levels gain `ordinal`
  (in addition to the existing `name`/`description`) plus the lineage tags, and `get`/`ls`/`find`
  now model levels with their own property set instead of the generic fallback — the matching
  read-side keys are additive, and `diff` reports changes to `hideMembers` and `ordinal` (#116).
- `set` on partitions now covers the rest of their writable scalar surface: `description`,
  `mode`, `dataView`, `retainDataTillForceCalculate` (calculated sources only, just as
  `expression` remains M-source-only), and `queryGroup`, which must name an existing query
  group and clears on an empty value. `diff` reports changes to them, and the unsupported-
  property hint omits source-bound tokens the targeted partition cannot take (#117).
- `set` on relationships now covers their writable scalar surface: `name`, `isActive`,
  `crossFilteringBehavior`, `fromCardinality`, and `toCardinality` were already settable but
  are now advertised in the set hint, and `securityFilteringBehavior`,
  `relyOnReferentialIntegrity`, and `joinOnDateBehavior` are new. `get`/`ls`/`find` JSON, CSV,
  and text output gain the matching read-side keys (additive), and `diff` reports changes to
  the security-filtering, referential-integrity, and date-join behavior (#118).
- `set` on security objects now covers their writable scalar surface: roles gain `modelPermission`
  (plus writable `name`/`description`), role members gain `memberId` and — external members only —
  `identityProvider` and `memberType`, with a clear error for Windows members, and table
  permissions gain `metadataPermission`. TOM freezes a member's identity once attached, so every
  member identity edit replaces the member, as renames already did. Role members now get their
  own property set in `get`/`ls`/`find` output (additive, replacing the generic fallback), and
  `diff` reports changes to the identity fields and both permission enums (#119).
- `set` on the model root and shared objects now covers their writable scalar surface: the `.`
  path accepts `compatibilityLevel` (already supported, now advertised), `description`,
  `culture`, `collation`, `discourageImplicitMeasures`, `discourageCompositeModels`,
  `defaultMode`, `defaultDataView`, `maxParallelismPerQuery`, `maxParallelismPerRefresh`,
  `sourceQueryCulture`, and `forceUniqueNames`, with `tx get .` reading the whole surface back;
  calculation-group tables accept `precedence`, calculation items accept `ordinal`, and data
  sources accept `maxConnections`, provider-only `impersonationMode`/`isolation`/`timeout`, and
  structured-only `contextExpression`, with the set hint omitting tokens the targeted source
  kind cannot take. Calculation items and data sources now get their own property sets in
  `get`/`ls`/`find` output (additive, replacing the generic fallback), and `diff` reports the
  semantic scalars. `discourageReportMeasures` stays read-only: TOM's setter demands the
  internal-only compatibility sentinel, so no real model can set it (#120).
- Contributor onboarding: bug-report and feature-request issue forms and a pull-request
  template carrying the reviewer checklist (docs page, command-surface snapshot, CHANGELOG),
  plus `scripts/dev.sh` / `scripts/dev.ps1` task runners (`build`, `test`, `format`,
  `snapshot`, `docs`) that work from any directory — including a cross-platform snapshot
  recipe, since `TOMIX_UPDATE_SNAPSHOTS=1 …` is not valid PowerShell (#147).
- CI hygiene: every workflow job now has a timeout and a concurrency group (releases and
  post-release smoke runs queue instead of being cancelled mid-publish), the NuGet cache keys
  on `Directory.Packages.props` so Dependabot bumps stop missing the cache, and the
  deploy-script selftest checker (`scripts/qa/selftest-checker.sh`) runs on the Linux CI leg (#147).

### Changed

- `set` no longer accepts `connectionString` on data sources: credentials are secrets, and
  secrets are never accepted via argv — edit the source file or use `tx script` to change them (#120).
- Destructive commands now ask for confirmation before running: `stage commit` (it overwrites
  the source and, for remote sources, deploys over the endpoint), `script --save` and `mv --save`
  (they overwrite the source and sync the workspace mirror), `bpa run --fix --allow-delete`
  (it deletes model objects), the `--revert` forms of `script`/`mv`/`bpa run` (they discard
  staged work), and the partition-risky `refresh` variants (`--type clearvalues`,
  `--skip-refresh-policy`/`--apply-refresh-policy false`, `--effective-date`). Routine refreshes,
  `--dry-run`, `--save-to` copies, and `--stage` (which defers to the `stage commit` gate)
  stay unprompted. In non-interactive contexts these commands fail fast with
  `TOMIX_CONFIRMATION_REQUIRED`; pass `--yes` to skip the prompt (#145).

### Removed

- The unused `coverlet.collector` reference from every test project: no workflow or script ever
  collected a report from it, so it was dead weight in every restore (#147).

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
- Contributor docs and scripts: `AGENTS.md` and the CONTRIBUTING files now list all source and
  test projects (including `Tomix.Platform`, `Tomix.Auth`, and `Tomix.Provider.Vpax`) and
  document the `dotnet format` CI gate, and the duplicated output/color routing rows in
  `AGENTS.md` are merged; `src/Tomix.Provider.Tmdl/CONTEXT.md` no longer describes the provider
  as read-only now that writes ship through `TmdlModelSession.SaveAsync`; and
  `scripts/install-dev.ps1` / `install-dev.sh` work from any working directory (#147).

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

[Unreleased]: https://github.com/bgarcevic/tomix-cli/compare/v0.6.0...HEAD
[0.6.0]: https://github.com/bgarcevic/tomix-cli/compare/v0.5.0...v0.6.0
[0.5.0]: https://github.com/bgarcevic/tomix-cli/compare/v0.4.3...v0.5.0
[0.4.3]: https://github.com/bgarcevic/tomix-cli/compare/v0.4.2...v0.4.3
[0.4.2]: https://github.com/bgarcevic/tomix-cli/compare/v0.4.1...v0.4.2
[0.4.1]: https://github.com/bgarcevic/tomix-cli/compare/v0.4.0...v0.4.1
[0.4.0]: https://github.com/bgarcevic/tomix-cli/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/bgarcevic/tomix-cli/compare/v0.2.1...v0.3.0
[0.2.1]: https://github.com/bgarcevic/tomix-cli/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/bgarcevic/tomix-cli/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/bgarcevic/tomix-cli/releases/tag/v0.1.0
