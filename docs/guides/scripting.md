# Output & scripting

`tx` is built to be piped. Two rules make that work:

1. **Data goes to stdout, diagnostics go to stderr.** You can pipe any
   command's output to `jq` without ever receiving a warning.
2. **Every command that prints a table also prints JSON or CSV** via
   `--output-format json|csv`. Model-shaped output can also be emitted as
   `tmdl` or `tmsl`/`bim`.

Errors follow the same contract: `--error-format json` emits a single JSON
error envelope on stderr (see the
[error codes reference](../error-codes.md#json-error-envelope)).

## Paths are the pipeline currency

`ls --paths-only` and `find --paths-only` emit one object path per line, and
most commands accept paths as input:

```sh
# Format every object whose expression mentions CALCULATE
tx find "CALCULATE" --in expressions --paths-only | xargs -I{} tx format --path "{}"

# Count columns per table
tx ls --type column --output-format json |
  jq '.data | group_by(.path | split("/")[0]) | map({(.[0].path | split("/")[0]): length}) | add'
```

## Querying live models

`query` runs DAX or DMV against a live model, with DAX Studio-style
performance options: `--trace` (formula- vs storage-engine timings), `--cold`
(clear the cache first), and `--runs N` (benchmark with Avg/Min/Max/StdDev).
Timings print to stderr, so the rowset on stdout stays pipeable; with `--output-format json`
they are folded into the result document instead.

```sh
# Server timings for a measure (needs workspace/server admin)
tx query 'EVALUATE ROW("Sales", [Total Sales])' --trace

# Benchmark a heavy query cold, five runs
tx query --file heavy.dax --cold --runs 5
```

`--trace` and `--cold` need write access to the model (workspace Admin,
Member or Contributor); Build permission alone is not enough. When they are
unavailable they warn and the query still returns its rows.

## Exit codes

| Code | Meaning |
|------|---------|
| 0 | Success. |
| 1 | General failure (most errors). |
| 2 | Usage/argument error, IO failure, or pre-condition violation. |

`diff` is the exception by design: `0` = identical, `1` = differences found,
`2` = error — so it drops straight into a CI gate.

## CI

Flags that matter in pipelines:

- `--non-interactive` — never prompts; fails with an error saying what to
  provide instead of hanging.
- `--quiet` — suppress spinners, progress, and hints.
- `--ci github` / `--ci vsts` on `validate`, `bpa run`, `test`, and `deploy` — print
  CI log-group commands to stderr, so findings annotate the PR.
- `--trx <path>` on `validate`, `test`, and `bpa run` — write results to a
  `.trx` test-run file.
- `--fail-on <level>` on `bpa run` — the lowest severity that fails the run.
- `--check` on `format` — change nothing and exit `1` when an expression is
  not formatted.
- Color is stripped automatically when output is not a TTY, and the
  [`NO_COLOR`](https://no-color.org/) convention is honored.

### Linting DAX on every PR

Three offline checks make a DAX linter, and none of them needs a server or
credentials:

- `tx validate` fails on broken DAX and M: syntax errors and references to
  tables, columns and measures that do not exist
  ([codes](../error-codes.md#validate-issue-codes)).
- `tx bpa run` checks the [best-practice rules](../commands/validate.md#bpa-best-practice-analyzer),
  including the DAX rules: `IFERROR`, `/` instead of `DIVIDE`, `INTERSECT`
  instead of `TREATAS`, unused variables, `CALCULATE` without filters, and more.
  Tune which rules run with `bpa rules ignore` and a rules file checked into the repo.
- `tx format --check` fails when a measure is not formatted the way `tx format`
  would format it.

```yaml
# GitHub Actions: lint a model on every PR
name: DAX lint
on: pull_request
jobs:
  lint:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x
      - run: dotnet tool install --global Tomix.Cli
      - run: tx validate ./MyModel.SemanticModel --ci github --non-interactive
      - run: tx bpa run ./MyModel.SemanticModel --ci github --fail-on warning --non-interactive
      - run: tx format ./MyModel.SemanticModel --check --non-interactive
```

```yaml
# Azure DevOps: the same checks, with BPA results published as a test run
- script: tx validate ./MyModel.SemanticModel --ci vsts --non-interactive
  displayName: Validate DAX
- script: >
    tx bpa run ./MyModel.SemanticModel --ci vsts --fail-on warning
    --trx $(Agent.TempDirectory)/bpa.trx --non-interactive
  displayName: Best practices
- script: tx format ./MyModel.SemanticModel --check --non-interactive
  displayName: DAX formatting
- task: PublishTestResults@2
  condition: always()
  inputs:
    testResultsFormat: VSTest
    testResultsFiles: $(Agent.TempDirectory)/bpa.trx
```

Run each check as its own step so one failure does not hide the others.

### DAX regression tests as a PR gate

`tx test` runs `.dax` queries with recorded expected results
([details](../commands/validate.md#test-dax-regression-tests)) and exits
`1` on any drift. Because tests execute on a deployed model, the PR gate is:
deploy the PR's model to a dev workspace, refresh it, then run the tests
against it. On Azure DevOps with a service principal:

```yaml
# Azure DevOps: deploy the PR model to the dev workspace, then gate on tests
- script: >
    tx test ./MyModel.SemanticModel/tests
    -s "$(DevWorkspace)" -d MyModel
    --auth spn --non-interactive
    --trx $(Agent.TempDirectory)/dax-tests.trx --ci vsts
  displayName: DAX regression tests
- task: PublishTestResults@2
  condition: always()
  inputs:
    testResultsFormat: VSTest
    testResultsFiles: $(Agent.TempDirectory)/dax-tests.trx
```

A non-zero exit fails the stage; `--ci vsts` additionally annotates each
failing test. Accept intentional result changes by re-running
`tx test --update` locally and committing the snapshot diff.

Ready-made workflow examples live in the
[samples folder](https://github.com/bgarcevic/tomix/tree/main/samples).

### Refresh-policy bootstrap

The expression evaluator does not execute `Model.Tables["Sales"].ApplyRefreshPolicy()`.
Use `tx refresh --table Sales --policy-only -s MyWorkspace -d MyModel` to apply a saved
policy without loading data. It previews and asks first; use `--yes` for noninteractive
execution. A subsequent `tx refresh --table Sales -s MyWorkspace -d MyModel` loads data.
