---
name: cli-ux-guidelines
description: >
  CLI UX rules for tomix, condensed from clig.dev. Consult this whenever adding or
  changing a command, option, argument, help text, output rendering, error message,
  prompt, exit code, or config/env handling in src/Tomix.Cli — even for small changes
  like rewording an error or adding a flag. Also use when reviewing PRs that touch
  Commands/ or Output/.
---

# CLI UX Guidelines (tomix)

Condensed from [Command Line Interface Guidelines](https://clig.dev) (CC BY-SA 4.0),
adapted to this repo. Where a rule is already enforced by shared infrastructure,
use that infrastructure — do not re-derive it in a command. See the wiring map at
the bottom for where each concern lives.

## Philosophy

- Human-first by default; machine-readable on request. A TTY means a human is reading.
- Simple parts that compose: output of `tx` should be usable as input to other tools.
- Be consistent with existing conventions (ours: the filesystem metaphor — `ls`, `get`,
  `rm`, `mv`, `find`) so users can guess commands they have never seen.
- A CLI is a conversation: confirm state changes, suggest the next command, make
  failure recoverable. Say just enough — silence looks broken, noise hides signal.

## The basics (non-negotiable)

- Parse with System.CommandLine only; never hand-parse argv.
- Exit 0 on success, non-zero on failure. Use the documented exit codes via
  `CommandOutput`; never invent ad-hoc codes in a command.
- Data goes to stdout. Messages — progress, warnings, errors, hints — go to stderr
  (`ErrorOutput` / the stderr `AnsiConsole`). A user piping `tx ls` to a file must
  get only the listing.

## Help

- `-h` / `--help` shows full help; running a parent command bare (e.g. `tx`) shows
  concise help. Never make bare invocation an error.
- Lead with examples. Every command's help gets an `Examples:` block showing the
  2–3 most common invocations; complex syntax (e.g. `ls` path filters) is taught
  by example, not by grammar.
- Order help by frequency of use: most common commands and flags first.
- Group subcommands into sections in root help (Explore / Edit / Remote / Quality /
  Workspace). A flat list of 30+ commands is unusable. [tomix: gap — implement in
  `SpectreHelpAction` / `HelpRenderer`.]
- On typo or invalid subcommand, suggest the closest match ("Did you mean `ls`?").
- Format help with the `Styling` role palette (see `docs/cli-color-strategy.md`);
  link to web docs at the end.

## Output

- Human-readable output is paramount; check whether stdout/stderr is a TTY and
  degrade gracefully when it is not.
- Machine output where it doesn't hurt humans: `--output-format json|csv` per
  command through `JsonOutput`/`CsvOutput`; `--paths-only`-style flags where a
  plain one-record-per-line form aids piping (clig's `--plain`).
- On success, print something brief — silence reads as a hang — but err toward
  less. Support `-q`/`--quiet` to suppress non-essential output. [tomix: gap.]
- If you change state, say exactly what changed and what the new state is
  (model object counts, target workspace, file written). Make current state easy
  to inspect (`session`, `stage`, `doctor` are our `git status` equivalents).
- Suggest the next command after workflow steps (Slate `Guidance`), and give every
  empty result a message plus a hint — never print nothing.
- Crossing the program boundary (network calls, writing files not named by the
  user) should be visible: name the server/file on stderr as it happens.
- Color with intention only, via `Styling` helpers — one warm accent per line,
  semantic roles only. Never hard-code ANSI or markup in commands.
- Disable color when: stdout/stderr is not a TTY (check each stream separately),
  `NO_COLOR` is set and non-empty, `TERM=dumb`, `--no-color`/config says so.
  [tomix: config handled in Program.cs; NO_COLOR + TERM are gaps — verify what
  Spectre detects natively before adding checks.]
- No spinners or animations when not a TTY (CI logs fill with frames otherwise).
- Don't print internals only the authors understand; debug detail belongs behind
  `--verbose`, not in default stderr.

## Errors

- Catch errors and rewrite them for humans. Every error answers: what happened,
  why, how to fix it. Never let a raw stack trace or XMLA/HTTP exception reach the
  user by default.
- Put the most important word first; users read the first line and the last line.
- Multiple errors: group and summarize; don't interleave with normal output.
- Unexpected errors: print a short message, write detail to a debug log or show it
  under `--verbose`, and tell the user where to file an issue.
- Render through `ErrorOutput` so `--error-format json` keeps working.

## Arguments and flags

- Prefer flags to positional arguments; flags self-document at the call site.
- Every flag has a full-length name; one-letter aliases only for the most-used
  flags. Use standard names where they exist: `-q/--quiet`, `-f/--force`,
  `-o/--output`, `--json`, `--no-color`, `--no-input`, `--version`, `--dry-run`.
- Two positional arguments meaning different things is a smell; more than two is
  a bug. (Multiple args of the *same* kind — file lists — are fine.)
- Defaults should be correct for the majority; flags adjust, never enable basic
  usability.
- Support `-` for stdin/stdout where a file path is accepted.
- Never accept secrets via flags (they leak into shell history and `ps`). Take
  them from files, prompts, or the credential store — and not from env vars either.
- Validate input early and report all problems with the offending value echoed back.

## Interactivity

- Prompt only when stdin is a TTY. When it is not, never block on a prompt — fail
  fast with the flag that would have answered it ("pass --yes to confirm").
- Never *require* interactivity; every prompt has a flag equivalent. Honor
  `--no-input` to forbid all prompting. [tomix: gap — add to `GlobalOptions`.]
- Confirm before anything destructive or remote-mutating (`rm`, `replace`,
  `deploy`): mild = y/n, severe = type the object/workspace name, catastrophic =
  require an explicit flag. Give `deploy` a `--dry-run` that prints the diff it
  would push. [tomix: gap.]
- Mask password input; let the user escape (Ctrl-C must always work in prompts).

## Subcommands

- Be consistent across subcommands: same flag names for the same concepts
  (`--model`, `--server`, `--output-format` are recursive globals — keep it that way).
- Keep one shape (`noun verb` like `bpa rules list`); no ambiguous near-duplicate
  names; no catch-all subcommand that guesses intent; no arbitrary prefix
  abbreviations (`tx dep` must not silently mean `deploy`).

## Robustness and responsiveness

- Responsive beats fast: print something within ~100 ms; show a Spectre `Status`/
  progress for anything over ~1 s (connects, formatter API calls, deploys) — TTY only.
- Network operations time out and fail with a actionable message; partial work is
  recoverable (idempotent retries; staged changes survive a crash).
- Validate user input; expect misuse; make operations crash-only where possible
  (no corrupt state if killed mid-run).
- On Ctrl-C, exit as soon as possible; print a note before any slow cleanup, and
  let a second Ctrl-C skip it.

## Future-proofing (output is a contract)

- JSON field names, exit codes, flag names, and subcommand names are public API.
  Keep changes additive; deprecate with a warning period before removing or
  renaming; changing human-oriented text is fine.
- Don't repurpose a flag to mean something different — add a new one.

## Versioning policy

- Versions are derived from git tags by [MinVer](https://github.com/adamralph/minver).
  Tag format: `v<major>.<minor>.<patch>` (e.g. `v1.2.3`). Pre-release tags:
  `v1.2.3-alpha.1`, `v1.2.3-beta.2`. Between tags, MinVer auto-increments
  pre-release identifiers based on commit count since the last tag.
- Bump by tagging — no file edits required. Push the tag to trigger the
  release workflow:
  - **Patch** (`v1.0.1`): bug fixes, no new flags/fields/exit codes.
  - **Minor** (`v1.1.0`): new commands, flags, JSON fields — backward-compatible.
  - **Major** (`v2.0.0`): removed/renamed a flag, changed a JSON field name or
    shape, changed an exit code, removed a subcommand.
- The API surface that major versions protect:
  - JSON output: field names, value types, envelope shape (`data`, `diagnostics`).
  - Exit codes: numeric values and their meanings (see `CommandOutput`).
  - CLI flags: names, aliases, value syntax, default behavior.
  - Subcommand names and `noun verb` structure.
- Human-readable output (colors, formatting, prose) is NOT covered by the
  compatibility guarantee and may change in any version.
- Maintain a `CHANGELOG.md` (Keep a Changelog format) in the repo root.
  Update it in the same PR that ships the change.

## Configuration and environment

- Precedence: flags > env vars > project config > user config > system config.
- Config files follow platform conventions (XDG on Linux); never modify another
  tool's config without explicit consent.
- Env vars are for context that varies per environment/session, not for secrets
  and not as the primary config store. Respect general-purpose ones: `NO_COLOR`,
  `TERM`, `EDITOR`, proxy vars, `TOMIX_*` for app-specific overrides.

## Wiring map (where rules live in this repo)

- Exit codes, format validation, human/JSON dispatch → `Output/CommandOutput.cs`
- Color roles, markup helpers, tables → `Output/Styling.cs` + `docs/cli-color-strategy.md`
- Diagnostics to stderr, `--error-format` → `Output/ErrorOutput.cs`
- JSON/CSV contracts → `Output/JsonOutput.cs`, `Output/CsvOutput.cs`
- Recursive global flags → `Commands/GlobalOptions.cs`
- Help rendering → `Output/HelpRenderer.cs` / `SpectreHelpAction`
- Version resolution (`--version`, `doctor`) → `Program.ResolveVersion()` reads `AssemblyInformationalVersionAttribute` set by MinVer
- Version derivation from git tags → `Directory.Build.props` (MinVer config)
- One `ICommandModule` per command; commands stay thin, no business logic.

## Known gaps checklist

- [x] Grouped sections + Examples blocks in help
- [x] `NO_COLOR` / `TERM=dumb` handling verified or added
- [x] Confirmations with `--yes` on `rm`, `replace`, `deploy`; `--dry-run` on `deploy`
- [x] `-q/--quiet` global flag (suppresses spinners, progress, non-essential output)
- [x] Empty-state messages with next-step hints on `ls`/`find`
- [x] "Did you mean?" suggestions for unknown subcommands
- [x] Spinners on slow commands (P0: deploy, bpa, connect, auth; P1: format, save, diff, validate, script, stage commit; P2: conditional for ls/get/find/deps/load/set/add/mv/rm/replace when remote or --save)
- [x] `refresh` command (live per-table rows via XMLA SessionTrace; final summary table)
- [ ] Ctrl-C handling audit on long-running remote operations
- [ ] `--no-input` global flag (covered by `--non-interactive`; adding a duplicate is confusing)
