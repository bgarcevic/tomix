# tomix

A command-line interface for semantic models. Browse, edit, lint, diff, and
deploy tabular models the way you work with files: `ls`, `get`, `find`, `rm`,
`mv` — against a TMDL folder, a `.bim` file, an XMLA endpoint, or a Power BI
Desktop instance running on your machine.

Semantic models are code. They deserve tooling that works where code lives:
in a terminal, in scripts, in CI, in a diff.

```
$ tx connect ./samples/basic-tmdl
Connected: basic-tmdl (TMDL folder)

$ tx ls --type table
Customers
Products
Sales

$ tx find "SUM(" --type measure
Sales/Total Amount      SUM(Sales[Amount])

$ tx bpa run
Warning  AVOID_FLOATING_POINT  Sales[Amount] uses double
1 warning, 0 errors. Run 'tx bpa run --fix' to apply auto-fixes.

$ tx deploy --server MyWorkspace --database basic-tmdl
OK Deployed basic-tmdl to MyWorkspace (4.1s)
```

Everything that prints a table also prints JSON or CSV (`--output-format
json`), so the output of any command can become the input of your next script.

## Install

Standalone binary, no runtime required:

```sh
# Linux / macOS
curl -LsSf https://raw.githubusercontent.com/bgarcevic/tomix-cli/main/install/install.sh | sh

# Windows
powershell -ExecutionPolicy ByPass -c "irm https://raw.githubusercontent.com/bgarcevic/tomix-cli/main/install/install.ps1 | iex"
```

If you have the .NET SDK: `dotnet tool install -g Tomix.Cli`. Archives for six
platforms are on the [releases page](https://github.com/bgarcevic/tomix-cli/releases);
checksums included.

Connecting to a locally running Power BI Desktop instance (`--local`) is
Windows-only. Everything that operates on TMDL/BIM files works everywhere.

## Commands

Explore: `ls`, `get`, `find`, `info`, `deps`
Edit: `add`, `set`, `mv`, `rm`, `replace`, `format` (DAX and M, via the
formatter APIs)
Quality: `bpa` (Best Practice Analyzer with auto-fix), `validate`, `diff`
Remote: `connect`, `auth`, `deploy`, `load`, `save`, `profile`
Workflow: `stage` (mutations are staged, then committed or discarded),
`session`, `macro`, `interactive`, `config`, `doctor`, `completion`

`tx <command> --help` shows options and examples. `tx doctor` checks your
environment when something seems off.

## Scripting

Object paths are the pipeline currency. `ls --paths-only` emits one path per
line; most commands accept paths as input:

```sh
# Format every measure that mentions CALCULATE
tx find "CALCULATE" --type measure --paths-only | xargs -I{} tx format -p "{}"

# Count columns per table
tx ls --type column --output-format json | jq 'group_by(.table) | map({(.[0].table): length})'
```

Exit codes are documented and stable. Errors go to stderr (as JSON if you
pass `--error-format json`), data goes to stdout.

## Status

This is a proof of concept under active development. The command surface is
settling but not settled; JSON field names and exit codes may still change
before 1.0. `query`, `refresh`, `test`, `vertipaq`, and `incremental-refresh`
exist as placeholders and are not implemented yet.

If you try it and something breaks or reads wrong, an issue with the output
of `tx doctor` attached is genuinely useful at this stage.

## Contributing

Build and test with `dotnet build && dotnet test`, then
`dotnet run --project src/Tomix.Cli -- doctor`. The architecture is documented
in `CONTEXT.md` files throughout the tree — start with
[`src/Tomix.Cli/CONTEXT.md`](src/Tomix.Cli/CONTEXT.md). Color and output
conventions live in [`docs/cli-color-strategy.md`](docs/cli-color-strategy.md).
See [CONTRIBUTING.md](CONTRIBUTING.md) for the rest.

## License

[MIT](LICENSE). Third-party components and the provenance of the bundled BPA rules are documented in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
