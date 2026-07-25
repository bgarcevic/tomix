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
| `-r, --rules <file>` | Path(s) or URL(s) to BPA rule file(s) in JSON format. |
| `--ruleset <name>` | Standard ruleset: `standard` (curated default), `full`, `microsoft`, `microsoft-it`, `microsoft-ja`, `microsoft-es`. |
| `--rule <id>` | Run only specific rule(s) by ID. |
| `--path <path>` | Limit analysis to matched objects (literal names, wildcards, or paths). |
| `--errors` / `--warnings` / `--info` | Show only rules of that severity (combinable). |
| `--fail-on <threshold>` | Failure threshold: `error` (default) or `warning`. |
| `--fix` | Apply fix expressions to auto-fix violations where possible. Destructive `Delete()` fixes are skipped unless `--allow-delete` is set. |
| `--allow-delete` | With `--fix`: also apply destructive `Delete()` fixes that remove model objects. Reference tracking cannot see report visuals or external consumers, so review staged changes before deploying. |
| `--save` / `--save-to <path>` | Persist the model after applying fixes. |
| `--details` / `--full` | Show full guidance per rule / list every affected object. |
| `--no-multiline` | Collapse each rule's guidance to a single line. Text output only. |
| `--no-model-rules` / `--no-defaults` | Exclude rules embedded in the model / the selected standard ruleset. |
| `--allow-external-rules` | Also load remote (URL) rule files referenced by the model's rule annotations. Skipped by default so a model file cannot make `tx` fetch arbitrary URLs. |
| `--ci <github\|vsts>` | Emit CI logging commands to stderr. |
| `--trx <path>` | Write results as a VSTEST `.trx` file. |

```sh
tx bpa run
tx bpa run --errors
tx bpa run --fix --save
```

`bpa rules` manages rule collections:

| Subcommand | Description |
|------------|-------------|
| `bpa rules list` | List BPA rules from all sources with status. With a model, also lists the model's embedded and external-file rules (remote URLs are reported, not fetched) and any rule-load diagnostics. |
| `bpa rules enable` / `bpa rules disable` | Re-enable or disable a built-in rule for the current user. |
| `bpa rules ignore` / `bpa rules unignore` | Add or remove a rule on the model's ignore list. |

`bpa rules --rules-file <file>` points the subcommands at a BPA rules JSON
file. `bpa rules list` narrows what is listed:

| Option | Description |
|--------|-------------|
| `--ruleset <name>` | Standard BPA ruleset to list: `standard`, `full`, `microsoft`, `microsoft-it`, `microsoft-ja`, `microsoft-es`. |
| `--no-defaults` | Suppress built-in rules from the output. |
| `--ignored` / `--disabled` | Show only ignored / only disabled rules. |
| `--all` | Show all rules including disabled and ignored. |

The BPA gate also runs automatically on `deploy` (`--skip-bpa` to bypass,
`--fix-bpa` to auto-fix first, `--bpa-rules` to point at specific rule files).
On `save`, BPA runs only when `--fix-bpa` is passed. The gate never applies
destructive `Delete()` fixes — those are only available via
`bpa run --fix --allow-delete`.

## `validate` — DAX and relationship integrity

```
tx validate [model] [options]
```

| Option | Description |
|--------|-------------|
| `--ci <github\|vsts>` | Emit CI logging commands to stderr so findings annotate the PR. |
| `--trx <path>` | Write results as a VSTEST `.trx` file. |
| `--errors-only` | Only show errors. |
| `--no-warnings` | Hide semantic-analyzer warnings. |
| `--server-only` | Only show errors reported by the connected server. |
| `--no-multiline` | Collapse multi-line cell content to a single line. Text output only. |

```sh
tx validate
tx validate --ci github
tx validate --trx results.trx
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
| `--ci <github\|vsts>` | Emit CI logging commands to stderr so failures annotate the PR. |
| `--trx <path>` | Write results as a VSTEST `.trx` file. |

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
| `--detail` | Expanded columns: data/dictionary/hierarchy size breakdown, encoding, segments. |
| `--stats` | Model-level storage summary. |
| `--top <n>` | Limit each view to the N largest rows. |
| `--fields <list>` | Comma-separated fields to display (single view; text/csv only). |
| `--export <file.vpax>` | Export statistics to a `.vpax` file. |
| `--import <file.vpax>` | Analyze a previously exported `.vpax` offline (no connection needed). |
| `--obfuscate` | Obfuscate names and expressions in the export; writes a private `.dict` dictionary. |
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

```sh
tx diff ./v1.tmdl ./v2.tmdl
tx diff ./v1.bim ./v2.bim --output-format json
```

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
