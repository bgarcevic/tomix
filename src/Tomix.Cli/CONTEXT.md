# Tomix.Cli

CLI entry point for `tx`.

## Responsibilities

- Define commands, options, arguments, help text, and aliases.
- Parse CLI input using `System.CommandLine`.
- Call application handlers in `Tomix.App`.
- Return documented exit codes.

## Cross-folder dependencies

- Depends on `/src/Tomix.App` for command behavior.
- Depends on `/src/Tomix.Core` for shared result and diagnostic types.
- Renders output in `Output/` (see Structure below).
- Depends on `/src/Tomix.Ui` for the localhost web endpoint of a shared session; `Serve/` plugs
  the session into it, so `Tomix.Ui` itself knows nothing of sessions.
- References `/src/Tomix.Provider.*` projects only so `Program` (the composition root) can
  construct providers and pass them to commands as `IModelProvider` lists. Feature logic must
  go through App/Core abstractions — never use provider-specific types in command modules.

## Structure

- `Commands/` - one `ICommandModule` per command. Each module builds its `Command`, parses input,
  constructs and calls its application handlers, selects an output renderer, and returns the exit
  code. Each module is the feature-level composition root; `Program` owns process-wide dependencies
  and registers modules. Commands may render prompts and trivial one-line messages; complex tables,
  trees, serialization, and projections live in `Output/`.
- `Commands/GlobalOptions` - the recursive global options; `Commands/LifecycleOptions` - the
  factory for the shared mutation lifecycle flags (`--save`, `--save-to`, `--serialization`,
  `--stage`, `--revert`, `--no-sync`). Mutating commands must take these from the factory rather
  than declaring their own copies, so descriptions stay uniform.
- `Interactive/` - `tx interactive` (alias `shell`): the read-run loop, the session-only commands
  (undo, redo, begin/commit/rollback, status, history, exit), and the line editor. A session runs
  the ordinary command modules, built by `Program.BuildSessionRootCommand` with a `SessionScope` so
  they lease the live session (`SessionScope.SourceFor`) and default to its model
  (`SessionScope.TryResolveModel`). A module that can run in a session takes an optional
  `SessionScope`; one that needs more than the model stays out of the session tree.
- `Serve/` - the session protocol (docs/protocol.md) for `tx serve` (stdio) and the shared
  localhost endpoint of `tx ui`. `ProtocolServer` owns the lifecycle, validation, cancellation and error
  answers over an `IMessageChannel` (`StreamChannel` frames on stdio, `WebSocketChannel`).
  `SessionHost` owns the one live session of the process, hands out client IDs, broadcasts every
  event and keeps the `/status` JSON; each connection is a `ServeSession`, which answers the
  methods, most by running the session command tree (the one `tx interactive` uses) with JSON
  output captured, so `data` is the command's own JSON. `ProtocolRoutes` maps parameters to
  command-line arguments. `ConsoleRouting` gives each running command its own captured console
  (async-local), so clients' requests run side by side and stdout carries only frames.
  `WebEndpoint` puts a host on `Tomix.Ui`; `LiveRegistry` writes `~/.tomix/live/*.json` so other
  processes find it. `ServeRelay` lets `tx serve` join a session `tx ui` holds instead of
  opening a second one. `UiLifetime` decides when `tx ui` stops: never with changes unsaved.
  `tx interactive`, `tx serve` and `tx ui` handle Ctrl+C themselves (the last two through
  `ConsoleSignals`), so `Program.TerminationTimeout` turns off the library's handling for them:
  every Ctrl+C handler in a process runs, and the library's would end the process two seconds
  later, before the session is closed or after a Ctrl+C meant for one command.
- `Output/` - shared output wiring used by every command. See `Output/CONTEXT.md` for details.
  - `OutputFormats` - the canonical `--format` option, aliases, and allowed values.
  - `JsonOutput` - the single JSON serializer (the `--format json` contract).
  - `CommandOutput` - format validation, human/JSON dispatch, diagnostic printing, exit-code mapping.
  - `Styling` - color palette and markup helpers. Single source of truth for all color/style decisions.

## Rules

- Keep command classes thin.
- Do not put business logic here.
- Keep handler construction inside the owning command module. Do not introduce a service locator or
  a container solely to move `new` expressions; inject process-wide dependencies into the module
  constructor and pass only the exact store or service required by each handler.
- `AppServices` is the explicit process-lifetime state bundle. `Program` selects its members while
  registering modules; command modules and application handlers must never receive the entire bundle.
- Do not access TOM, Power BI, XMLA, BIM, or TMDL APIs directly.
- Do not hand-roll JSON output inside commands; serialize through `Output/JsonOutput`.
- Add a new command as its own `ICommandModule` in `Commands/`; reuse `Output/` rather than re-deriving format handling.
- CLI help, JSON field names, diagnostics, and human output must avoid versioned third-party product names or abbreviations for licensing-sensitive compatibility work.
- Use the color palette and helpers in `Output/Styling.cs`. See `/docs/cli-color-strategy.md` for the full palette, message categories, and migration status.
- Do not hard-code ANSI escape codes or Spectre markup strings in commands. Use `Styling` helpers.
- Consult `/docs/cli-ux-guidelines.md` when adding or changing any command, option, argument, help text, output rendering, error message, or exit code.

## Test

```bash
dotnet build
dotnet test
dotnet run --project src/Tomix.Cli -- doctor
```
