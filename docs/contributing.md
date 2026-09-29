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
source. `./scripts/dev.sh` (`.\scripts\dev.ps1` on Windows) wraps the common
chores — `build`, `test`, `format`, `snapshot`, `docs` — and works from any
directory. CI runs `dotnet format --verify-no-changes` as a required check,
so run the `format` task before pushing. The sample model at
`samples/basic-tmdl` is the standard fixture for manual testing.

## How the code is organized

```
src/Tomix.Cli        CLI surface: parsing, rendering, exit codes. No business logic.
src/Tomix.App        Application handlers: one handler per operation.
src/Tomix.Core       Domain model, provider abstractions.
src/Tomix.Platform   Dependency-free filesystem and OS primitives shared by outer projects.
src/Tomix.Provider.* Model providers (TMDL folders, TOM/XMLA, VPAX).
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
- **A conventional PR title** — `fix(bpa): ...`, `feat: ...`, `docs: ...`. The
  title becomes the commit subject and decides the next version. A breaking
  change (`feat!: ...`) needs the `breaking-approved` label from a maintainer.
- **A changelog entry** — changes under `src/` add a line under `[Unreleased]`
  in `CHANGELOG.md`, written for users. That section is the release notes;
  with no entry there, the merge produces no release. If a release lands while
  your PR is open, merge `main` in and check that your entry is still under
  `[Unreleased]`, not the section the release just cut.

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
help-snapshot test in `Tomix.Cli.Tests` will remind you if you forget, and
`./scripts/dev.sh snapshot` (`.\scripts\dev.ps1 snapshot` on Windows)
regenerates the snapshot.

The terminal screenshots and GIFs in `docs/assets/media` are recorded from
the real CLI, not drawn by hand. When a command's output changes, re-record
them with `uv run scripts/docs-media/record.py` (or name one scene, e.g.
`... record.py bpa`). The scenes — commands, sample model, window size — live
in `scripts/docs-media/scenes.json`. Recording runs on Windows, macOS, and
Linux; it needs the .NET SDK and a monospace font (Cascadia Mono preferred).

## Bugs and ideas

Open an [issue](https://github.com/bgarcevic/tomix-cli/issues). For bugs,
include the output of `tx doctor`, the command you ran, and what you
expected. Issues labeled `good first issue` are scoped to be doable without
understanding the whole codebase.
