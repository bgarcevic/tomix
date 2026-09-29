# tomix

A command-line interface for semantic models. Browse, edit, lint, diff, and
deploy tabular models the way you work with files: `ls`, `get`, `find`, `rm`,
`mv` — against a TMDL folder, a `.bim` file, an XMLA endpoint, or a Power BI
Desktop instance running on your machine.

Semantic models are code. They deserve tooling that works where code lives:
in a terminal, in scripts, in CI, in a diff.

![tx connecting to a PBIP model, searching expressions, and tracing a measure's dependencies](assets/media/explore.gif)

```console
$ tx connect ./samples/basic-tmdl
Model: basic-tmdl
  CL: 1601
  tables: 3  measures: 4  relationships: 2  roles: 0

Active: ./samples/basic-tmdl

$ tx ls --type table --paths-only
Customers
Products
Sales

$ tx find "SUM" --in expressions
╭───────────────────┬─────────┬────────────┬───────┬──────╮
│ Path              │ Type    │ Property   │ Match │ Line │
├───────────────────┼─────────┼────────────┼───────┼──────┤
│ Sales/Total Sales │ Measure │ Expression │ SUM   │ 1    │
╰───────────────────┴─────────┴────────────┴───────┴──────╯
1 match(es)

$ tx bpa run
BPA analysis · basic-tmdl

● WARNING  3 rules · 9 objects
  ×4  Hide foreign keys
      HIDE_FOREIGN_KEYS · Formatting · fixable
  ×4  Provide format string for measures
      PROVIDE_FORMAT_STRING_FOR_MEASURES · Formatting
  ×1  Model should have a date table
      MODEL_SHOULD_HAVE_A_DATE_TABLE · Performance

────────────────────────────────────────────────────────────────────────────────
✗ 9 warnings in 3 of 26 rules · 23 passed · 159ms

Fix 4 findings:  tx bpa run --fix --save
Details:         tx bpa run --details
One rule:        tx bpa run --rule HIDE_FOREIGN_KEYS

$ tx deploy --server MyWorkspace --database basic-tmdl
OK Deployed basic-tmdl to MyWorkspace (4.1s)
```

Every command prints JSON with `--output-format json`, so the output of any
command can become the input of your next script. `ls`, `get`, `query`,
`refresh`, `save`, and `vertipaq` also print CSV, and `get` can emit
the model formats too (`tmdl`, `bim`, `tmsl`).

## Where to start

- **[Installation](getting-started/installation.md)** — standalone binary,
  `dotnet tool`, or build from source.
- **[Quickstart](getting-started/quickstart.md)** — connect to a sample model
  and run your first commands in five minutes.
- **[Commands](commands/index.md)** — the full command surface, grouped the
  same way as `tx --help`.
- **[Output & scripting](guides/scripting.md)** — pipe `tx` into `jq`, `xargs`,
  and CI.

## Status

This is a proof of concept under active development. The command surface is
settling but not settled; JSON field names and exit codes may still change
before 1.0.

If you try it and something breaks or reads wrong, an
[issue](https://github.com/bgarcevic/tomix-cli/issues) with the output of
`tx doctor` attached is genuinely useful at this stage.

## License

[MIT](https://github.com/bgarcevic/tomix-cli/blob/main/LICENSE). Third-party
components and the provenance of the bundled BPA rules are documented in
[THIRD-PARTY-NOTICES.md](https://github.com/bgarcevic/tomix-cli/blob/main/THIRD-PARTY-NOTICES.md).
