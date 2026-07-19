# How to contribute

The full contribution guide lives in
[CONTRIBUTING.md](https://github.com/bgarcevic/tomix-cli/blob/main/CONTRIBUTING.md)
in the repository — this page is the short version.

## Getting set up

You need the .NET 10 SDK. Then:

```sh
dotnet build
dotnet test
dotnet run --project src/Tomix.Cli -- doctor
```

If `doctor` is happy, you're ready. For the inner loop, `./tx <command>`
(`.\tx.ps1` on Windows) wraps `dotnet run` and always reflects your current
source. The sample model at `samples/basic-tmdl` is the standard fixture for
manual testing.

## How the code is organized

```
src/Tomix.Cli        CLI surface: parsing, rendering, exit codes. No business logic.
src/Tomix.App        Application handlers: one handler per operation.
src/Tomix.Core       Domain model, provider abstractions.
src/Tomix.Provider.* Model providers (TMDL folders, TOM/XMLA).
src/Tomix.Auth       Authentication and credential caching.
tests/               Core, App, CLI, TOM, TMDL, and VPAX test projects.
```

Each directory has a `CONTEXT.md` describing its responsibilities and
conventions — read the one for the area you're changing before you start.

## What reviews check for

- **The stdout/stderr contract** — data on stdout, diagnostics on stderr.
- **Stable machine output** — JSON field names, exit codes, flag names, and
  command names are treated as public API.
- **UX conventions** — the [CLI UX guidelines](cli-ux-guidelines.md) are the
  condensed rulebook; the [color strategy](cli-color-strategy.md) covers
  what colors mean.
- **Tests accompany behavior.**
- **Scope** — small, focused PRs merge fast; open an issue first for
  anything larger than a single command or fix.

## Working on these docs

The documentation site is built with [Zensical](https://zensical.org) from
the `docs/` folder and `zensical.toml`. You need
[uv](https://docs.astral.sh/uv/) — it manages the Python side for you:

```sh
uv run zensical serve                  # live-reloading preview
uv run zensical build --clean --strict # what CI runs
```

The site deploys to GitHub Pages automatically on every push to `main` that
touches `docs/` or `zensical.toml`. When you add, remove, or change a command
or its options, update the matching page under `docs/commands/` — the
help-snapshot test in `Tomix.Cli.Tests` will remind you if you forget.

## Bugs and ideas

Open an [issue](https://github.com/bgarcevic/tomix-cli/issues). For bugs,
include the output of `tx doctor`, the command you ran, and what you
expected. Issues labeled `good first issue` are scoped to be doable without
understanding the whole codebase.
