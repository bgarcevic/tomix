---
name: tomix
description: Inspects, edits, lints, diffs, queries, and deploys Power BI, Fabric, and Analysis Services semantic models with the tomix CLI (`tx`). Use when working with TMDL folders, .bim files, PBIP projects (*.SemanticModel), a running Power BI Desktop model, or an XMLA endpoint, or when the user mentions measures, DAX, calculated columns, relationships, Best Practice Analyzer (BPA), VertiPaq, or deploying or refreshing a semantic model. Not for Power BI report visuals or layout (*.Report folders), or for Power Query authoring beyond formatting M.
---

# tomix (`tx`)

`tx` treats a semantic model like a file tree. Objects have slash paths
(`Sales`, `Sales/Total Sales`, `Sales/Measures`). Data goes to stdout, diagnostics go to stderr.

Check the install with `tx doctor`. If `tx` is missing, ask the user to install it:
https://bgarcevic.github.io/tomix-cli/getting-started/installation/

## How to call it

- Add `--non-interactive` to every command.
- Pick the output format by what you need from it:
  - Plain text is fine for overviews such as `summary`, `deps`, `bpa run` or `validate`, and it is
    usually smaller.
  - Add `--output-format json` when you need exact values (expressions, format strings, query
    numbers) or will parse or pipe the result. JSON is the stable contract; text layout can change.
  - `ls` and `find` tables shorten long expressions. Read one in full with `tx get <path>`.
- Errors arrive on stderr as one JSON object: `{"error", "code", "severity", "hint"}`.
  Read `hint` before retrying. Codes are listed at
  https://bgarcevic.github.io/tomix-cli/error-codes/
- Exit codes:
  - `0` ok, `1` failure, `2` usage or precondition error.
  - `3` from `tx deploy` or `tx refresh` without `--yes`: the preview ran and nothing was applied.
    That is the expected result of a preview, not an error.
  - `tx diff` exits `1` when the models differ. That is a result, not a failure.
- Quote paths that contain spaces: `tx get "Sales/Net Sales"`. DAX form also works:
  `"'Sales'[Net Sales]"`.

## Connect once, then omit the model

```bash
tx connect ./MyModel.SemanticModel        # TMDL / PBIP folder or .bim file
tx connect MyWorkspace "Sales Model"      # XMLA: workspace + semantic model
tx connect --local --list                 # running Power BI Desktop instances (Windows)
tx connect                                # show what is connected
```

The connection is scoped to the git repository or worktree. Every later command uses
it unless you pass a model path. To work on two models from one repository, set
`TOMIX_SESSION=<name>` per terminal. In scripts and CI, pass `-m <path>` on each
command instead of connecting.

## Read before you change anything

```bash
tx summary                                   # where the model lives, object counts
tx ls --type table --paths-only
tx ls "Sales/Measures"
tx get "Sales/Total Sales"                   # all properties of one object
tx get "Sales/Total Sales" --query expression
tx get --where "isHidden=false" --type column --paths-only
tx find "CALCULATE" --in expressions --paths-only
tx deps "Sales/Total Sales" --deep           # what it uses and what uses it
tx get --unused                              # measures and columns nothing depends on
```

Use `tx` to read the model, not `cat` or `grep` on the TMDL files. It resolves
references, works the same on files and live models, and returns one shape for all of
them.

## Change the model

Edit commands only preview by default and write nothing. Add `--save` to write the
change to the model's source.

```bash
tx add "Sales/Margin %" -t Measure -e "DIVIDE([Margin], [Total Sales])" --set formatString="0.0%"
tx set "Sales/Total Sales" --set description="Net sales after returns"
tx mv "Sales/Amt" "Sales/Amount"             # rewrites DAX references to the renamed object
tx rm "Sales/Old Measure"                    # refuses while DAX still references it
tx replace "Sales[Amt]" "Sales[Amount]" --in expressions
tx format --path "Sales/Total Sales"
```

Workflow for a change:

1. Run the command without `--save` and read the preview.
2. Run it again with `--save`. For several related edits, use `--stage` on each one and
   then `tx stage commit`. `tx stage status` shows what is queued, and
   `tx stage discard` drops it.
3. Run `tx validate`. Fix every new error before you report the change as done.

Make renames, moves, and deletes with `tx mv` and `tx rm`, not by editing TMDL files.
`tx` updates the DAX references, and a manual file edit leaves them broken. If you
do edit TMDL files directly, run `tx validate` afterwards.

Stop and ask the user first when `tx` refuses a change and the only way forward is
`--force`, `--allow-delete`, or `--no-fix-refs`.

## Live sessions (`tx mcp`, `tx ui`)

When the harness lists tomix MCP tools (`session_open`, `object_get`, `object_set`, ...),
edit through them rather than through one-shot commands. Edits apply at once as undo
steps and stay unsaved until `session_save`. If the person has the model open in `tx ui`,
they see each edit as it happens.

- Read with `object_get`, `object_find`, `model_tree` and `deps_get`. They see unsaved
  edits, and a `cat` of the TMDL files doesn't.
- Wrap related edits in `transaction_begin` (with a label) and `transaction_commit`, so
  they undo as one step.
- While a session is open, never edit its `.tmdl` or `.bim` files directly. It would save
  over them.
- Saving:
  - For a TMDL folder or `.bim` file, call `session_save` when the task is done and
    `dax_check` shows no new errors. Git and `session_undo` can take it back, and a
    session that `tx mcp` holds on its own discards unsaved edits when it stops.
  - For a server or Power BI Desktop model, ask the person before `session_save`.
    It changes the live model, which has no git history.
- On `TOMIX_SESSION_STALE`, the model changed outside the session. Tell the person,
  and let them choose between reloading and overwriting.

While `tx ui` holds a model, one-shot `tx add`, `set`, `mv`, `rm`, `get`, `ls`, `find`,
`validate` and the other model commands run in its session instead of on the files.
Their edits stay unsaved until `tx save`, and `--stage` is refused.

## Best Practice Analyzer

```bash
tx bpa run                                   # findings, grouped by rule
tx bpa run --fix                             # preview the fixes
tx bpa run --fix --save                      # apply them
tx bpa rules show <RULE_ID>                  # what a rule checks and why
```

Run `tx bpa run --fix` (preview) before `--save`, and tell the user which rules it
fixed. Don't ignore a rule (`tx bpa rules ignore`) unless the user asks for it.

## Live models: query, test, VertiPaq

These commands need a deployed model or Power BI Desktop. They don't work on TMDL or BIM
files.

```bash
tx query 'EVALUATE ROW("Sales", [Total Sales])' --output-format json
tx query --file ./queries/top-products.dax --limit 100
tx vertipaq --tables --top 10 --output-format json
tx test ./tests                              # DAX regression snapshots
```

Use `--limit` on exploratory queries so the output stays small.

## Compare and deploy

```bash
tx diff ./main/Model.SemanticModel ./feature/Model.SemanticModel
tx deploy --server MyWorkspace --database "Sales Model" --xmla -    # TMSL preview only
tx deploy --server MyWorkspace --database "Sales Model"             # preview without --yes
```

`tx deploy` and `tx refresh` change shared workspaces that other people use. Show the user the
target workspace, the model, and the `--xmla` preview or the diff. Pass `--yes` only after the
user confirms that exact target in this conversation. Don't add `--deploy-*`
overwrite flags or `--skip-bpa` unless the user asks for them.

## Task references

- CI pipelines (GitHub Actions, Azure DevOps): [references/ci.md](references/ci.md)
- Full command reference: https://bgarcevic.github.io/tomix-cli/commands/
