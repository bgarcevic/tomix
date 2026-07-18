# Quickstart

This walkthrough uses the sample model that ships with the repository. Clone
it (or download [`samples/basic-tmdl`](https://github.com/bgarcevic/tomix-cli/tree/main/samples))
if you want to follow along exactly — every command works the same against
your own TMDL folder or `.bim` file.

## 1. Connect to a model

`connect` sets the **active connection** for your terminal session, so you
don't have to repeat the model path on every command:

```console
$ tx connect ./samples/basic-tmdl
Model: (unnamed)
  CL: 1601
  tables: 3  measures: 4  relationships: 2  roles: 0

Active: ./samples/basic-tmdl
```

You can also point commands at a model explicitly with `-m/--model`, or at a
deployed model with `-s/--server` and `-d/--database` — see
[Connections & sessions](../guides/connections.md).

## 2. Look around

```sh
tx ls                          # everything, as a table
tx ls --type table --paths-only
tx ls Sales/Measures           # children of a container
tx get "Sales/Total Sales"     # all properties of one object
tx get "Sales/Total Sales" -q expression
```

Object paths are slash-separated (`Sales/Total Sales`); DAX-style forms like
`'Sales'[Total Sales]` are accepted too. Quote names with spaces.

## 3. Search

```sh
tx find "SUM" --in expressions
tx deps "Sales/Total Sales" --upstream    # what does this measure use?
tx deps --unused                          # unreferenced measures and columns
```

## 4. Make a change

Mutations preview by default; nothing touches disk until you say so:

```sh
# Preview: shows the result without saving
tx add "Sales/Net Sales" -t Measure -i "SUM(Sales[Amount]) - SUM(Sales[Discount])"

# Persist it
tx add "Sales/Net Sales" -t Measure -i "SUM(Sales[Amount]) - SUM(Sales[Discount])" --save
```

For multi-step edits, stage mutations and commit them as a batch — see
[Editing & staging](../guides/editing.md).

## 5. Lint it

```sh
tx bpa run              # Best Practice Analyzer
tx bpa run --fix        # auto-fix what can be fixed
tx validate             # DAX and relationship integrity
```

## 6. Deploy it

```sh
tx deploy --server MyWorkspace --database basic-tmdl
tx refresh --type full
```

Deploys run the BPA gate first (`--skip-bpa` to bypass), and `--dry-run`
previews what would change on the remote target.

## Where next

- [Commands overview](../commands/index.md) — the full surface.
- [Output & scripting](../guides/scripting.md) — JSON/CSV output, piping,
  exit codes, CI.
- `tx <command> --help` — options and examples for any command.
