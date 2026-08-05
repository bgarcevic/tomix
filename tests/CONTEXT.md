# tests

Automated tests for Tomix.

## Responsibilities

- Unit tests.
- Application handler tests.
- CLI smoke tests.
- Output-contract tests (`Tomix.Cli.Tests/GetLsParityTests` pins the get/ls JSON+CSV contract; `Tomix.App.Tests/PropertyCatalogTests` pins the per-kind property sets).
- Provider fixture tests.
- Optional integration tests.

## Cross-folder dependencies

- `/tests/Shared` holds BCL-only support code compiled into every test assembly by
  `tests/Directory.Build.props`. It is not a project, so no test project depends on another; keep it
  free of `Tomix.*` references so it stays usable from all six. Helpers that need a production type
  belong in the owning project's `Support/` folder instead.
- `/tests/Tomix.Core.Tests` tests Core types and production project-dependency boundaries.
- `/tests/Tomix.App.Tests` tests App handlers, shared platform primitives, authentication, and
  cross-provider application flows.
- `/tests/Tomix.Cli.Tests` tests `/src/Tomix.Cli`, including the JSON/CSV output contracts.
- `/tests/Tomix.Provider.Tom.Tests` tests the TOM file/server adapter.
- `/tests/Tomix.Provider.Tmdl.Tests` tests TMDL model opening and mapping.
- `/tests/Tomix.Provider.Vpax.Tests` tests VPAX import/export and statistics mapping.

## Rules

- Default `dotnet test` should be fast and deterministic.
- Do not require external services for normal test runs.
- Use integration tests only when credentials are available.
- Update output-contract tests only when output changes intentionally.

## Writing and maintaining tests

### Where a test belongs

Put a test in the project that owns the type under test, following
`Cross-folder dependencies` above. A test for a Core type belongs in `Tomix.Core.Tests` even if
you found the bug through a handler; a test for the JSON output shape belongs in `Tomix.Cli.Tests`,
because the serializer lives in `/src/Tomix.Cli/Output`. Two projects testing the same production
type is duplication — merge into the owning project.

### A test must be able to fail

Before you commit a test, make it fail on purpose: break the production code it guards, confirm the
failure message is actionable, then restore. Guards that cannot fail are worse than no test, because
they read as coverage. Concretely, avoid:

- **Self-referential assertions.** Asserting a property of a value the test itself supplied — a regex
  against an `[InlineData]` literal, `Assert.Null` on something passed as `null`, or checking a
  hardcoded item is absent from a hardcoded set. Assert against something *production* produced.
- **Mirrored production configuration.** Never rebuild a copy of production's options/wiring in the
  test. `Tomix.Cli.Tests/MutationResultContractTests` serializes through `JsonOutput` — the code path
  the commands use — because a locally-built `JsonSerializerOptions` with a blanket
  `DefaultIgnoreCondition` silently made every "field is omitted" assertion pass regardless of the
  `[JsonIgnore]` attributes it was supposed to be pinning.
- **Assertions weaker than the test name.** If the name promises a behavior, assert that behavior.

### Prefer `[Theory]` for input matrices

When tests differ only in an input value, an object kind, or a property name, collapse them into a
`[Theory]` with `[InlineData]`/`[MemberData]` rows. Adding the next case should be a one-line data
row, not a copy-pasted method. Keep separate `[Fact]`s when each case asserts a *distinct* message or
failure mode — that distinction is the documentation.

### Reuse the shared helpers

Check for an existing helper before writing setup. Never hand-roll
`Path.GetTempPath()` + `Directory.CreateDirectory` + `try`/`finally` + `Directory.Delete`, a
`Console.SetOut`/`SetError` redirect, a root command, or a `..`-relative path to `samples/`.

Available to every test project (`tests/Shared`, compiled in via `tests/Directory.Build.props`;
namespace `Tomix.Tests.Support` is a global using):

- `TempDir` — a throwaway directory that exists on construction and is deleted on dispose, plus
  `Combine`/`CreateSubdirectory`/`WriteFile`. `using var dir = new TempDir();`
- `SampleModel` — `Locate()` for `samples/basic-tmdl`, `Locate(name)` for any other sample,
  `CopyToTemp()`/`CopyTo(...)` for a writable copy, `CopyDirectory(...)`. Never open a sample in
  place in a test that saves.
- `RepoPaths` — `Root` (upward search anchored on `Tomix.slnx`), `Samples`, `Combine(...)`.

Per project:

- `Tomix.App.Tests/Support/TempConfigDir` — throwaway config dir with cached
  `State`/`Staging`/`Stores`. `using var config = new TempConfigDir();`
- `Tomix.App.Tests/Support/QueryStubs` — query-capable session/provider stubs (`Session` with
  `OnQuery`/`Result`/`Throw`, `Provider`, `NonQueryProvider`, `ThrowingProvider`).
- `Tomix.App.Tests/Support/MutationStubs` — `SnapshotSession` (records set/rewrite calls and their
  order), `MoveCapableSnapshotSession`, `Provider`, and the `BaseAndDerived()` snapshot.
- `Tomix.Cli.Tests/ConsoleCapture` — `Run(Func<int>) -> (ExitCode, Stdout, Stderr)`, restoring the
  writers in a `finally`; pass `captureAnsiConsole: true` for Spectre-rendered output. Put the class
  in `ConsoleStateCollection`.
- `Tomix.Cli.Tests/TestRoot` — `With(...)` for a root carrying the global options plus the
  subcommands under test, `Full()` for the production tree, `Descendants(...)` to walk it.
- `Tomix.Cli.Tests/TestServices` — `AppServices` rooted in a throwaway temp dir, so command tests
  never touch the developer's real `~/.tomix`.
- `Tomix.Provider.Tom.Tests/TestModels` — in-memory TOM fixtures (`NewDatabase`, `NewTable`,
  `AddTable`, `WithSales`, `WithRelationship`) and the `Add`/`Remove`/`Move`/`Set`/`Replace` request
  factories. Imported as a static using, so call them unqualified.
- `Tomix.Provider.Vpax.Tests/TestDaxModelBuilder` — DAX model fixtures.

If you need the same fixture or stub twice, extract it rather than copying — duplicated fixtures
drift apart silently. Anything that creates files must clean up after itself.

A class-scoped `Directory.CreateTempSubdirectory(...)` field deleted in `Dispose` is also fine when
every test in the class shares one directory; reach for `TempDir` when the directory is per-test.

### Contract, snapshot, and drift-guard tests

Some tests exist to fail loudly when a public surface moves; do not weaken them to make a change
pass. Change the production behavior deliberately, then update the pin in the same commit.

- `Tomix.Cli.Tests/GetLsParityTests` — get/ls JSON+CSV contract.
- `Tomix.App.Tests/PropertyCatalogTests` — per-kind property sets.
- `Tomix.App.Tests/ErrorCodeContractTests` — every `TOMIX_*` literal in `src/` is documented in
  `docs/error-codes.md`, and retired codes are not re-emitted.
- `Tomix.Provider.Tom.Tests/CatalogSearchableAgreementTests`, `CatalogWritableAgreementTests` —
  catalog drift.
- `Tomix.Cli.Tests/CommandSurfaceSnapshotTests` — the CLI surface. Regenerate deliberately:

```bash
TOMIX_UPDATE_SNAPSHOTS=1 dotnet test --filter CommandSurfaceSnapshotTests
```

Redundancy is acceptable in a pin when the duplicate carries information the general assertion
cannot — a named test whose failure message documents a specific past regression is worth keeping
even if a broader deep-equality test already covers it. Say so in a comment.

### Keep it fast

The whole suite runs in seconds; keep it that way. No network, no `Process.Start`, no `dotnet run`,
no `Thread.Sleep`. Use a fake `HttpMessageHandler` for HTTP and inject time/progress rather than
waiting on it. Load a sample model once per class (`IClassFixture`) only when the tests are
read-only — most provider tests mutate their `Database` in place, so a shared model would couple
them.

### Adding a test for a new command

Cover the handler in `Tomix.App.Tests` (behavior, error codes, save/stage/revert wiring) and the CLI
in `Tomix.Cli.Tests` (parsing, exit codes, rendered output). Update the matching page in
`/docs/commands` and regenerate the command-surface snapshot.

## Test

```bash
dotnet test
```
