# ADR 0004: Performance budgets for the live model session

- **Status:** Proposed
- **Date:** 2026-10-08
- **Issue:** [#352](https://github.com/bgarcevic/tomix-cli/issues/352), part of epic [#341](https://github.com/bgarcevic/tomix-cli/issues/341)
- **Answers:** the performance questions that [ADR 0001](adr-0001-live-model-session.md) §3 and §7, [ADR 0002](adr-0002-live-session-lease-gate-and-journal-first.md) and [ADR 0003](adr-0003-live-session-checkpoint-rollback.md) left to #352. It changes none of their decisions.

## Context

Earlier ADRs set a 50 ms budget for an edit and left three questions open until a large model could be measured:

- **Snapshots (ADR 0001 §3).** Is rebuilding the whole snapshot after a change fast enough, or must snapshots become incremental?
- **Derived data (ADR 0001 §7).** Is recomputing dependencies, DAX diagnostics and BPA in full after a change fast enough, or must it become incremental?
- **Checkpoints (ADR 0003).** Every writing transaction clones the model before its first write. Does that clone fit in the edit budget, or must the next checkpoint be taken right after a commit, before the next request arrives?

## Decision

### 1. Budgets

These are measured on a warm session, on the model in §2, as the median of 20 runs.

| Operation | Budget |
|---|---|
| An edit (`object.set`) until `model.changed` reaches the client | < 50 ms |
| `session.undo` | < 50 ms |
| A tree page (`model.tree` for the root, then for one table) | < 30 ms |

Opening a session, saving, a cold `bpa.run` and the derived-data recompute have no budget. The benchmark reports them so that changes stay visible.

### 2. The benchmark

- **Model.** `LargeModel` in `tests/Tomix.Cli.Tests/Performance` generates the model on the fly, so no sample is committed:
  - 100 tables of 30 columns and 10 measures each, which makes 3,000 columns and 1,000 measures;
  - 99 relationships;
  - half the measures aggregate a column, and half reference other measures.
- **Harness.** `LiveSessionBenchmarkTests` drives the dispatcher that `tx serve`, `tx mcp` and `tx ui` share (`SessionHost`, `ServeSession`), in process. It warms each operation up before timing it.
- **Running it.** The benchmark is opt-in and does not run in CI. Timings on shared runners are too noisy to gate on, and the purpose here is to answer the questions above. Run it on a quiet machine:

  ```bash
  TOMIX_PERF=1 dotnet test tests/Tomix.Cli.Tests -c Release --filter FullyQualifiedName~LiveSessionBenchmark --logger "console;verbosity=detailed"
  ```

  It prints the table below and fails when a budget in §1 is missed.

### 3. Results

These are the medians of three Release runs on an AMD Ryzen AI 7 PRO 350 with 24 GB, under Windows 11 and .NET 10, on 2026-10-08.

| Operation | Median (ms) | Budget (ms) |
|---|---:|---:|
| Session open (cold) | 240–330 | – |
| `object.set` + `model.changed` | 89–106 | < 50, **missed** |
| `session.undo` | 54–55 | < 50, **missed** |
| `model.tree` (root + one table) | 0.1 | < 30 |
| Snapshot rebuild after an edit | 7–10 | – |
| `bpa.run`, cold (after an edit) | 270–290 | – |
| Derived-data recompute (deps, DAX, BPA) | 235–450 | – |
| `bpa.run`, warm (recomputed) | < 0.1 | – |
| `session.save` | 150–260 | – |
| Provider set + commit, without the command layer | 45–140 | – |
| TOM `Database.Clone()` (one checkpoint) | 29–57 | – |

The p95 values reach several hundred milliseconds. Each checkpoint allocates a full copy of the model, and the undo stack keeps up to 50 of them, so gen-2 garbage collections land on random edits.

### 4. What follows from the results

- **Snapshots stay full rebuilds (ADR 0001 §3).** A rebuild takes 7–10 ms on 1,000 measures, well inside the budget. Incremental snapshots are not needed.
- **Derived data stays a full recompute (ADR 0001 §7).** It takes 0.25–0.45 s, off the lease queue and after the 250 ms debounce, and no request waits for it. A client that asks again gets the warm answer in under 0.1 ms. Incremental recompute is not needed.
- **The edit budget is missed, and checkpoints are the largest single cost (ADR 0003).**
  - One clone takes 30–57 ms, about a third of an edit, and more than half of an undo, which pays for a restore clone as well.
  - The clones also drive the garbage-collection pauses behind the p95.
  - The other half of an edit is in the command layer: the snapshot read before and after the change, validation, and the handler.
  - [#423](https://github.com/bgarcevic/tomix-cli/issues/423) moves the checkpoint off the request path and trims the command layer:
    - take the next checkpoint right after a commit, as ADR 0003 allows;
    - cap the memory the undo stack keeps on large models.
  - The benchmark is its acceptance test.
- **Query stays on the gate (ADR 0002).** A query needs a server, which this benchmark does not use. Nothing measured here argues for a second connection, so that question stays open until a server-side measurement does.

## Alternatives considered

- **A gate in CI.** It was rejected for now. Timings on shared runners vary by more than the budgets, so the gate would either flake or need margins too wide to catch anything. The benchmark can move into CI once the edit budget is met and the variance is known.
- **A committed large sample.** It was rejected. Generating the model costs under a second and keeps megabytes of TMDL out of the repository.

## Consequences

- The edit and undo budgets are not met on large models yet. The benchmark fails on them until [#423](https://github.com/bgarcevic/tomix-cli/issues/423) lands.
- Any change to the live session can be checked against the budgets with one command.
