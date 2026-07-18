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
tx find "CALCULATE" --in expressions --paths-only | xargs -I{} tx format -p "{}"

# Count columns per table
tx ls --type column --output-format json |
  jq 'group_by(.path | split("/")[0]) | map({(.[0].path | split("/")[0]): length}) | add'
```

## Querying live models

`query` runs DAX or DMV against a live model, with DAX Studio-style
performance options: `--trace` (formula- vs storage-engine timings), `--plan`
(logical and physical query plans), `--cold` (clear the cache first), and
`--runs N` (benchmark with Avg/Min/Max/StdDev). Timings and plans print to
stderr, so the rowset on stdout stays pipeable; with `--output-format json`
they are folded into the result document instead.

```sh
# Server timings + query plan for a measure (needs workspace/server admin)
tx query -q 'EVALUATE ROW("Sales", [Total Sales])' --trace --plan

# Benchmark a heavy query cold, five runs
tx query --file heavy.dax --cold --runs 5
```

The trace-based options require admin rights on the endpoint; when
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

- `--non-interactive` — disable all prompts; fail with an actionable error
  instead of hanging.
- `--quiet` — suppress spinners, progress, and hints.
- `--ci github` / `--ci vsts` on `validate` and `deploy` — emit GitHub
  Actions / Azure DevOps logging commands to stderr, so findings annotate
  the PR.
- `validate --trx <path>` — write results as a VSTEST `.trx` file.
- Color is stripped automatically when output is not a TTY, and the
  [`NO_COLOR`](https://no-color.org/) convention is honored.

```yaml
# GitHub Actions: lint and validate a model on every PR
- run: tx bpa run -m ./model.tmdl --non-interactive
- run: tx validate -m ./model.tmdl --ci github --non-interactive
```

Ready-made workflow examples live in the
[samples folder](https://github.com/bgarcevic/tomix-cli/tree/main/samples).
