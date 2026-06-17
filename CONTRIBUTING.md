# Contributing to tomix

Thanks for considering it. This file is a map, not a rulebook — the detailed
architecture docs already live in the tree as `CONTEXT.md` files, and they are
kept current. When this file and a `CONTEXT.md` disagree, trust the
`CONTEXT.md` and send a PR fixing this one.

## Getting set up

You need the .NET 10 SDK. Then:

```sh
dotnet build
dotnet test
dotnet run --project src/Tomix.Cli -- doctor
```

If `doctor` is happy, you're ready. For the inner loop, `./tx <command>`
(`.\tx.ps1` on Windows) is a thin wrapper around `dotnet run` — short to type
and always reflects your current source. To try your build as a genuinely
globally installed tool, `scripts/install-dev.ps1` (Windows) or
`scripts/install-dev.sh` (macOS/Linux) packs and installs it from source.

The sample model at `samples/basic-tmdl` is the standard fixture for manual
testing: `dotnet run --project src/Tomix.Cli -- connect ./samples/basic-tmdl`.

Note: anything touching the TOM provider or `--local` (Power BI Desktop
discovery) only runs on Windows. Everything else, including the full test
suite, works on Linux and macOS.

## How the code is organized

```
src/Tomix.Cli        CLI surface: parsing, rendering, exit codes. No business logic.
src/Tomix.App        Application handlers: one handler per operation.
src/Tomix.Core       Domain model, provider abstractions.
src/Tomix.Provider.* Model providers (TMDL folders, TOM/XMLA).
src/Tomix.Auth       Authentication and credential caching.
tests/             Tomix.Cli.Tests and Tomix.App.Tests, mirroring the source tree.
```

Each directory has a `CONTEXT.md` describing its responsibilities and
conventions. Read the one for the area you're changing before you start —
they're short, and reviewers will assume you have. Start with
[`src/Tomix.Cli/CONTEXT.md`](src/Tomix.Cli/CONTEXT.md).

## Adding a command

`LsCommand` is the model citizen; copy its shape (`src/Tomix.Cli/Commands/LsCommand.cs`
→ `Tomix.App.Ls.LsModelHandler`). It shows the current conventions: an optional
model argument that falls back to the session-resolved connection, path
filtering, and machine-friendly output flags. If your command mutates the
model, also look at how `set`/`rm` route changes through the staging flow
(`stage` → `commit`/`discard`) rather than writing directly.

1. **Module** — add a class in `src/Tomix.Cli/Commands/` implementing
   `ICommandModule`. `Build()` declares the `Command`, its arguments and
   options (use the shared option factories like `OutputFormats.CreateOption()`
   and `GlobalOptions` rather than redefining flags), and wires `SetAction`.
2. **Handler** — the action validates input, then delegates to a handler in
   `src/Tomix.App/<Feature>/` (`XHandler.HandleAsync(XRequest, ct)`). Commands
   stay thin: parse, call handler, render. If you're writing a loop or a
   conditional that isn't about parsing or rendering, it belongs in the handler.
3. **Render** — return through `CommandOutput.Render(result, format, ...)`,
   which handles exit codes, `--output-format json`, and error dispatch. Human
   output goes through `Styling` helpers only — no raw Spectre markup or ANSI
   in commands. Colors mean things here; see
   [`docs/cli-color-strategy.md`](docs/cli-color-strategy.md).
4. **Register** — add the module to the array in `Program.cs`.
5. **Test** — handler tests in `Tomix.App.Tests` for the logic, CLI tests in
   `Tomix.Cli.Tests` for parsing and output shape.

## What reviews check for

- **The stdout/stderr contract.** Data on stdout, diagnostics on stderr.
  Someone piping your command's output to `jq` must never receive a warning.
- **Stable machine output.** JSON field names, exit codes, flag names, and
  command names are treated as public API. Additive changes are fine;
  renames and removals need discussion in an issue first.
- **UX conventions.** [`docs/cli-ux-guidelines.md`](docs/cli-ux-guidelines.md)
  is the condensed rulebook (errors say what/why/how-to-fix, empty results
  print a hint, prompts never block in non-interactive contexts, and so on).
- **Tests accompany behavior.** A PR that changes behavior without touching
  tests will be asked about it.
- **Scope.** Small, focused PRs merge fast. If you're planning something
  larger than a single command or fix, open an issue first so we agree on the
  approach before you spend the time.

## AI-assisted contributions

This repo is deliberately agent-friendly — that's what the `CLAUDE.md`,
`AGENTS.md`, and `CONTEXT.md` files are for, and plenty of its own code was
written with AI assistance. AI-assisted PRs are welcome on the same terms as
any other: you've run it, you've tested it, you understand it well enough to
answer review questions about it. "The model wrote it" is not an answer in
review; unreviewed bulk output wastes everyone's time and will be closed.

## Bugs and ideas

Open an issue. For bugs, include the output of `tx doctor`, the command you
ran, and what you expected — `--error-format json` output is ideal. For
ideas, describe the workflow you're trying to achieve rather than the flag
you want; it usually leads somewhere better.

Issues labeled `good first issue` are scoped to be doable without
understanding the whole codebase.

## License

MIT. By submitting a contribution you agree it's licensed under the project's
[LICENSE](LICENSE). No CLA.
