# tomix

A command-line interface for semantic models. Browse, edit, lint, diff, and
deploy tabular models the way you work with files: `ls`, `get`, `find`, `rm`,
`mv` — against a TMDL folder, a `.bim` file, an XMLA endpoint, or a Power BI
Desktop instance running on your machine.

Semantic models are code. They deserve tooling that works where code lives:
in a terminal, in scripts, in CI, in a diff.

```console
$ tx connect ./samples/basic-tmdl
Model: (unnamed)
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
45 findings · 11 errors · 11 warnings · 23 info · 9 rules
19 of 45 can be auto-fixed — run  bpa run --fix

$ tx deploy --server MyWorkspace --database basic-tmdl
OK Deployed basic-tmdl to MyWorkspace (4.1s)
```

Everything that prints a table also prints JSON or CSV (`--output-format json`),
so the output of any command can become the input of your next script.

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
