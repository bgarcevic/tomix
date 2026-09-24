# Validate

Commands for checking model quality — locally, against a live model, and in
CI. See [Output & scripting](../guides/scripting.md#ci) for the CI-specific
flags.

## `bpa` — Best Practice Analyzer

```
tx bpa run [model] [options]
tx bpa rules <subcommand>
```

`bpa run` evaluates the model against a rule collection and reports findings
by severity; `--fix` applies auto-fixes where the rule provides one
(`FixExpression`).

The default `standard` ruleset is a curated high-signal subset of the bundled
catalog — rules that catch broken models, expensive-at-scale patterns, and a
small core of consumer-experience checks. Use `--ruleset full` for the entire
bundled catalog (including style and advisory rules).

The bundled catalog is embedded in the application and cannot be overridden by
placing a file beside the executable. Use `--rules`, model rule annotations, or
the `bpa rules` commands for explicit customization.

Models can carry their own rules: the `BestPracticeAnalyzer` annotation embeds
rule definitions directly, and the `BestPracticeAnalyzer_ExternalRuleFiles`
annotation lists rule files to load. Relative external-file paths resolve
against the model's folder (not the current directory), and Windows-style
separators (`..\.devops\bpa-rules.json`) work on every platform. When the same
rule ID appears in more than one source, the higher-precedence source wins:
ruleset < user rules (`--rules`, config-dir `bpa-rules.json`) < external files
< model-embedded — so a model's own rules always override the ruleset copy.
Among multiple external files, earlier entries in the annotation win. To detach
a model from an external rule file, remove the annotation:

```sh
tx set . -q annotation:BestPracticeAnalyzer_ExternalRuleFiles -i "" --save
```

| Option | Description |
|--------|-------------|
| `-r, --rules <file>` | BPA rule files or URLs, as JSON. |
| `--ruleset <name>` | Standard ruleset: `standard` (curated default), `full`, `microsoft`, `microsoft-it`, `microsoft-ja`, `microsoft-es`. |
| `--rule <id>` | Run only specific rule(s) by ID. |
| `--path <path>` | Limit analysis to matched objects (literal names, wildcards, or paths). |
| `--errors` / `--warnings` / `--info` | Show only rules of that severity (combinable). |
| `--fail-on <threshold>` | Failure threshold: `error` (default) or `warning`. Rules that cannot be evaluated count as error-severity findings and block under either threshold. |
| `--fix` | Fix violations whose rules provide a fix expression. Destructive `Delete()` fixes are skipped unless `--allow-delete` is set. |
| `--allow-delete` | With `--fix`: also apply destructive `Delete()` fixes that remove model objects. Reference tracking cannot see report visuals or external consumers, so review staged changes before deploying. |
| `--save` / `--save-to <path>` | Persist the model after applying fixes. |
| `--details` / `--full` | Show full guidance per rule / list every affected object. |
| `--no-multiline` | Show each rule's guidance on one line. Text output only. |
| `--no-model-rules` / `--no-defaults` | Skip rules embedded in the model / leave the selected standard ruleset out. |
| `--allow-external-rules` | Also load remote (URL) rule files referenced by the model's rule annotations. Skipped by default so a model file cannot make `tx` fetch arbitrary URLs. |
| `--ci <github\|vsts>` | Print CI log-group commands to stderr. |
| `--trx <path>` | Write results to a `.trx` test-run file. |

```sh
tx bpa run
tx bpa run --errors
tx bpa run --fix --save
```

`bpa run --fix --allow-delete` deletes model objects, so it asks for
confirmation; `--revert` (drops staged work) asks too. Pass `--yes` to skip
the prompt in scripts.

`bpa rules` manages rule collections:

| Subcommand | Description |
|------------|-------------|
| `bpa rules list` | List rules from every source, with each rule's status. With a model, also lists the model's embedded and external-file rules (remote URLs are reported, not fetched) and any rule-load diagnostics. |
| `bpa rules enable` / `bpa rules disable` | Turn a built-in rule back on, or off, for this user. |
| `bpa rules ignore` / `bpa rules unignore` | Add or remove a rule on the model's ignore list. |

`bpa rules --rules-file <file>` points the subcommands at a BPA rules JSON
file. `bpa rules list` narrows what is listed:

| Option | Description |
|--------|-------------|
| `--ruleset <name>` | Standard BPA ruleset to list: `standard`, `full`, `microsoft`, `microsoft-it`, `microsoft-ja`, `microsoft-es`. |
| `--no-defaults` | Leave the built-in ruleset out of the listing. |
| `--ignored` / `--disabled` | List only ignored / only disabled rules. |
| `--all` | Include disabled and ignored rules in the listing. |

The BPA gate also runs automatically on `deploy` (`--skip-bpa` to bypass,
`--fix-bpa` to auto-fix first, `--bpa-rules` to point at specific rule files,
`--bpa-fail-on` to lower the blocking threshold to warnings — errors block by default).
The deploy gate honors `bpa rules disable` for the rules it loads. `bpa run` can
load additional user and model rules, so the commands may report different findings.
A rule that cannot be compiled or evaluated is itself an error-severity finding
("rule could not be evaluated: \<reason\>"), so a typo in a rule expression fails
`bpa run` and the gates instead of silently skipping the rule.
On `save`, BPA runs only when `--fix-bpa` is passed; rules that cannot be
evaluated block the save even when other fixes were applied.
The gate never applies
destructive `Delete()` fixes — those are only available via
`bpa run --fix --allow-delete`.

## `validate` — DAX and relationship integrity

```
tx validate [model] [options]
```

Offline analysis runs on every DAX-bearing expression (measures, calculated
columns/items/tables, role filters, secondary measure expressions). Syntax is
checked first: illegal characters, unterminated string/table/bracket literals
and block comments, and unbalanced parentheses or braces are reported as
`DAX0004`/`DAX0005` errors — and when an expression's syntax is broken, its
reference checks are skipped, since a never-closed bracket makes everything
after it read wrong. Syntactically valid expressions then get the offline
reference checks (`DAX0001`–`DAX0003`, see [error codes](../error-codes.md)).

In text output each finding shows the offending expression line under its
message, syntax-highlighted when it came from DAX; `--no-multiline` collapses
the cell back to one line. Every issue carries a severity: `--output-format
json` includes it as a string (`"severity": "Error"` / `"Warning"`) on each
issue, CI annotations emit errors as errors and warnings as warnings (the run
still exits `1` only for errors), and the TRX projection maps each issue's
severity to its test outcome.

| Option | Description |
|--------|-------------|
| `--ci <github\|vsts>` | Print CI log-group commands to stderr so findings annotate the PR. |
| `--trx <path>` | Write results to a `.trx` test-run file. |
| `--errors-only` | Only show errors. |
| `--no-warnings` | Leave out analyzer warnings. |
| `--server-only` | Only show errors reported by the connected server. |
| `--no-multiline` | Show multi-line cell content on one line. Text output only. |

```sh
tx validate
tx validate --ci github
tx validate --trx results.trx
```

The banner names the model being validated: the TOM database name, falling
back to the Fabric `.platform` displayName, then to the file/folder name —
so a nameless TMDL folder still shows its own name instead of `(unnamed)`.

```console
$ tx validate ./samples/basic-tmdl
Validating...
Validating: basic-tmdl

No validation errors found.
```

## `test` — DAX regression tests

```
tx test [path] [options]
```

Runs DAX regression tests against a live model and exits `1` on any mismatch,
so a pipeline can block a merge when a query result drifts. Each test is a
`.dax` file paired with a sibling `<name>.expected.json` snapshot; `path` is a
single test file or a directory searched recursively for `.dax` files
(default: the current directory). Test names are the file paths relative to
`path`, without the extension (`totals/sales-by-region`).

| Option | Description |
|--------|-------------|
| `--update` | Record mode: run each query and (re)write its `.expected.json` from the actual result. Byte-identical snapshots are left untouched. |
| `--filter <pattern>` | Run only tests whose name matches a `*` wildcard pattern (case-insensitive). |
| `--param <name=value>` | Query parameter applied to every test, referenced as `@name` in DAX. Repeatable. |
| `--max-rows <n>` | Per-query row cap; a query exceeding it fails as an error (default: `10000`). |
| `--ci <github\|vsts>` | Print CI log-group commands to stderr so failures annotate the PR. |
| `--trx <path>` | Write results to a `.trx` test-run file. |

Like `query`, tests execute on a **deployed model** (XMLA) or a local
instance — never on TMDL/BIM files. Target the model with `-s <workspace>
-d <model>` or the active session.

**Workflow.** Add a test by writing a `.dax` file (any `EVALUATE` query),
then record its snapshot:

```sh
tx test ./tests --update -s MyWorkspace -d MyModel   # record snapshots
tx test ./tests -s MyWorkspace -d MyModel            # verify: all PASS, exit 0
```

A test without a snapshot fails the run as `MISS` (a new test must not
silently pass); a result that drifts from its snapshot fails as `FAIL` with a
difference table. Accept an intentional change by re-running `--update` and
committing the snapshot diff — the git diff *is* the review artifact.

The snapshot stores column names/types and all cell values as canonical
invariant strings (`null` = DAX `BLANK()`), plus a hash of the query text
used to hint when a failing test's query changed since recording:

```json
{
  "version": 1,
  "querySha256": "9f2c…",
  "columns": [
    { "name": "Sales[Region]", "type": "string" },
    { "name": "[Total Sales]", "type": "decimal" }
  ],
  "rows": [
    ["East", "1234.50"],
    ["West", null]
  ]
}
```

Rows compare **in order** — always end test queries with `ORDER BY` so
results are deterministic. Keep rowsets small and stable (aggregates,
`TOPN` over ordered sets); one `EVALUATE` rowset per file.

```sh
tx test ./tests
tx test ./tests/totals/sales.dax --update
tx test ./tests --filter "totals/*" --trx results.trx --ci vsts
```

## `vertipaq` — storage statistics

```
tx vertipaq [table] [options]
```

VertiPaq Analyzer-style storage statistics for a live model, with `.vpax`
export/import for offline analysis.

| Option | Description |
|--------|-------------|
| `--tables` / `--columns` / `--relationships` / `--partitions` / `--all` | Which view(s) to show (`--columns` is the default). |
| `--detail` | Extra columns: data/dictionary/hierarchy size breakdown, encoding, segments. |
| `--stats` | Model-level storage summary. |
| `--top <n>` | Limit each view to the N largest rows. |
| `--fields <list>` | Comma-separated fields to display (single view; text/csv only). |
| `--export <file.vpax>` | Export statistics to a `.vpax` file. |
| `--import <file.vpax>` | Analyze a previously exported `.vpax` offline (no connection needed). |
| `--obfuscate` | Mask names and expressions in the export; a private `.dict` dictionary keeps the mapping. |
| `--annotate` | Write statistics into the model as `Vertipaq_*` annotations (preview unless `--save`). |

```sh
tx vertipaq
tx vertipaq Sales --detail
tx vertipaq --stats --all --top 10
tx vertipaq --export stats.vpax
tx vertipaq --import stats.vpax --relationships
```

## `diff` — compare two models

```
tx diff <left> <right>
```

Compares two models (TMDL folders or `.bim` files) and shows structural
differences. Exit codes are CI-friendly: `0` = identical, `1` = differences
found, `2` = error.

When one side is a live database, engine-computed state is ignored so that comparing
source files against a processed model reports only authored changes. An inferred measure
data type present on only one side, calculated-table columns present on only one side,
and data type differences between calculated-table columns present on both sides are
not reported. Other column property changes remain visible. Comparing two authored
sources reports these differences because they may be authored changes.

```sh
tx diff ./v1.tmdl ./v2.tmdl
tx diff ./v1.bim ./v2.bim --output-format json
```

In JSON output, each row of `data.changes` has the following fields. The
`objectType` and `path` meanings depend on the action:

| Action | `objectType` | `path` | `oldValue` / `newValue` |
|--------|--------------|--------|-------------------------|
| `added` | Object kind, for example `Measure`. | Object path, for example `Sales/Total Sales`. | Omitted. |
| `removed` | Object kind. | Object path. | Omitted. |
| `modified` | Object kind followed by `/` and the object path, for example `Measure/Sales/Total Sales`. | Property name, for example `Description`. | Previous and new property values; each field is omitted individually when its value is null. |

Scripts should use this current shape until a future major release introduces
`objectPath` and `property` with a deprecation window for the existing fields.
The replacement schema and release timing will be decided in that later work.

## `doctor` — environment check

```
tx doctor
```

Checks whether the local tomix environment is ready. When filing a bug,
attach its output.

`doctor` is strictly local and deterministic: it checks config-directory
read/write access, configuration validity, profiles, sessions, cached
authentication metadata, registered providers, terminal capabilities, and the
cached update record. It never opens the OS keystore, refreshes credentials, or
contacts a model/release service. Terminal capabilities are included in both
text and JSON output. Warnings exit `0`; any failed health check exits `1`.

It remains runnable when `config.json` is corrupt so the report can identify
the failure and direct recovery with `tx config init --force`.
