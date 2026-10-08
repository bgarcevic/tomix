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

### 1. Targets

These are measured on a warm session, on the model in §2, as the median of 20 runs. An edit and an undo are timed once the session is idle, as a person's next edit finds it. Edits sent back to back are reported too, without a target.

| Operation | Target |
|---|---|
| An edit (`object.set`) until `model.changed` reaches the client | < 50 ms |
| `session.undo` | < 50 ms |
| A tree page (`model.tree` for the root, then for one table) | < 30 ms |

Opening a session, saving, a cold `bpa.run` and the derived-data recompute have no target. The benchmark reports them so that changes stay visible.

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

  It prints the table below and marks each target in §1 that is missed. The targets guide the work rather than gate it, so a miss does not fail the run; a failed request does.

### 3. Results

These are the medians of three Release runs on an AMD Ryzen AI 7 PRO 350 with 24 GB, under Windows 11 and .NET 10, on 2026-10-08. The first column is from #352, and the second is after [#423](https://github.com/bgarcevic/tomix-cli/issues/423) (§4).

| Operation | Median (ms), #352 | Median (ms), #423 | Target (ms) |
|---|---:|---:|---:|
| Session open (cold) | 240–330 | 230–255 | – |
| `object.set` + `model.changed` | 89–106 | 49–53 | < 50, met on some runs |
| `object.set` + `model.changed`, back to back | – | 62–69 | – |
| `session.undo` | 54–55 | 28–33 | < 50 |
| `model.tree` (root + one table) | 0.1 | 0.1 | < 30 |
| Snapshot rebuild after an edit | 7–10 | 12–18 | – |
| `bpa.run`, cold (after an edit) | 270–290 | 345–360 | – |
| Derived-data recompute (deps, DAX, BPA) | 235–450 | 265–325 | – |
| `bpa.run`, warm (recomputed) | < 0.1 | < 0.1 | – |
| `session.save` | 150–260 | 200–230 | – |
| Provider set + commit, without the command layer | 45–140 | 8–12 | – |
| TOM `Database.Clone()` (one checkpoint) | 29–57 | 24–27 | – |

In #352, the p95 values reached several hundred milliseconds. Each checkpoint allocates a full copy of the model, and the undo stack kept up to 50 of them, over 500 MB on this model, so gen-2 garbage collections landed on random edits. After #423, the p95 of an edit is 60–86 ms, and the undo stack keeps 23 steps in a managed heap of about 310 MB.

### 4. What follows from the results

- **Snapshots stay full rebuilds (ADR 0001 §3).** A rebuild takes 7–10 ms on 1,000 measures, well inside the budget. Incremental snapshots are not needed.
- **Derived data stays a full recompute (ADR 0001 §7).** It takes 0.25–0.45 s, off the lease queue and after the 250 ms debounce, and no request waits for it. A client that asks again gets the warm answer in under 0.1 ms. Incremental recompute is not needed.
- **Checkpoints were the largest single cost of an edit, and #423 moved them off the request path (ADR 0003).**
  - In #352, one clone took 30–57 ms, about a third of an edit. An undo cloned twice: once to keep the redo step, and once to restore. The clones also drove the garbage-collection pauses behind the p95.
  - **The next checkpoint is taken ahead.** After a commit, the session takes the next transaction's checkpoint in the background, once no request waits for the lease gate, so requests keep their place in the queue. A rollback, an undo or a redo leaves the checkpoint it restored ready, at no cost. The next write starts from the ready checkpoint, and an undo keeps it as its redo step, so neither clones on the request path. Any write, a save and a refresh drop it.
  - **The undo stack is capped by size as well as by count.** It keeps at most 50 steps and at most 100,000 objects across their checkpoints, about 250 MB, dropping the oldest steps first and always keeping the latest. Small models keep 50 steps; the benchmark model keeps 23.
  - Edits sent back to back still wait for the checkpoint the previous edit started, because the clone cannot be interrupted. That is the 62–69 ms row.
  - The rest of an edit, about 40 ms, is in the command layer: the snapshot read before and after the change, validation, and the handler. It stays as it is while an edit is near the target.
- **Query stays on the gate (ADR 0002).** A query needs a server, which this benchmark does not use. Nothing measured here argues for a second connection, so that question stays open until a server-side measurement does.

## Alternatives considered

- **A gate in CI.** It was rejected for now. Timings on shared runners vary by more than the budgets, so the gate would either flake or need margins too wide to catch anything. The benchmark can move into CI once the edit budget is met and the variance is known.
- **A committed large sample.** It was rejected. Generating the model costs under a second and keeps megabytes of TMDL out of the repository.

## Consequences

- Since #423, an edit sits at the 50 ms target on the benchmark model and undo is well inside it. The tail latency and the memory of the undo stack are bounded.
- A large model keeps fewer undo steps than a small one.
- The session holds the lease gate for a moment after each commit while it takes the next checkpoint, so a request arriving in that moment waits for it, as it would have waited to take the checkpoint itself.
- Any change to the live session can be checked against the targets with one command.
