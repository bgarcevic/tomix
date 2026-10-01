# ADR 0002: Lease gate and journal first for the live model session

- **Status:** Proposed
- **Date:** 2026-10-01
- **Issue:** [#343](https://github.com/bgarcevic/tomix-cli/issues/343), part of epic [#341](https://github.com/bgarcevic/tomix-cli/issues/341)
- **Supersedes:** [ADR 0001](adr-0001-live-model-session.md) §3 (threading model), the threading parts of §4 (cancellation and explicit grouping), and the order of work in its Consequences section. All other decisions in ADR 0001 stand.

## Context

ADR 0001 gives each live session an actor: a dedicated thread with a `Channel<SessionWorkItem>`, where every mutation, undo, save and TOM read is a work item. It also makes each top-level request one transaction and one undo step, and lets handlers stay unaware of whether their session is one-shot or live.

Starting #343 showed that these two decisions pull against each other:

1. **One request makes several session calls.** A rename is `SetProperty` or `MoveObject`, followed by `RenameFixup` calling `RewriteExpressions`. `BpaFixer` calls `RewriteExpressions` in a loop. If each call is its own work item, a request is several transactions, unless handlers open transactions themselves, and handlers are meant to stay session-agnostic.
2. **Running the whole request on the actor needs a custom synchronization context.** The other way to keep a request in one work item is to run the handler's body on the actor. Handlers are async, so their continuations would leave the actor thread unless the actor installs a single-threaded `SynchronizationContext`. That is the most complex part of the design and buys nothing TOM needs: TOM is not thread-safe, but it has no thread affinity either.
3. **The mutation interfaces are synchronous.** `IModelMutationSession.SetProperty` and its siblings return results directly. Calling them on an actor from another thread means blocking that thread until the work item finishes.

ADR 0001 §4 also has #344 route every TOM write through `TomChangeJournal`, and #344's own text says change events come from mutation results. Doing the journal inside #344 makes that PR the largest and riskiest in the epic. Deriving events from mutation results instead (or from a snapshot diff) means shipping production code that the journal replaces in #346.

## Decision

### 1. A lease gate replaces the actor

- **Each live session has one lease gate:** a FIFO async lock. All access to the TOM `Database` happens while holding it. Leases are granted in request order. A caller cancelled before its lease is granted is dropped, and nothing of it ran.
- **A lease is a transaction.** Acquiring a lease opens a transaction. `CommitAsync` commits it: the version moves by one, the change batch is published and an undo step is recorded. Disposing a lease without committing rolls back everything done through it. A request that throws partway, or is cancelled partway, therefore leaves the model as it found it. This is the same request-level guarantee ADR 0001 §4 asks for, at the same boundary where #345's `IModelSessionSource.LeaseAsync` already sits.
- **Isolation is enforced by structure.**
  - `ILiveModelSession` exposes only what needs no TOM access: state, version, events and the published snapshot.
  - Mutations, saves, queries, refresh and export are reachable only through `ILiveSessionLease.Session`, a capability view that handlers type-test exactly as they type-test sessions today.
  - The view throws `ObjectDisposedException` once its lease ends, so a reference kept past the lease cannot touch TOM. Its own `DisposeAsync` does nothing. Ending the lease is the lease's job, and closing the model is the host's.
- **Reentrancy joins, never deadlocks.** A lease requested from an asynchronous flow that already holds one joins the outer transaction. A joined lease's commit folds its changes into the outer transaction. Its rollback reverts only its own changes, which works like a savepoint.
- **Explicit transactions are long-held leases.** `BeginTransactionAsync` returns a lease that stays granted across requests until it is committed or rolled back. While it is open, leases from the same client join it and every other client's lease waits. Explicit transactions are flat, so a nested `begin` fails. The host ends an explicit transaction that sits idle past a timeout by rolling it back. #346 picks the default.
- **Reads stay lock-free on snapshots.** After each commit, the session publishes an immutable `LiveModelSnapshot` tagged with the new version. It is built on first request and cached. Building it takes the gate briefly, because it reads TOM. Every request after that reads the cached snapshot without waiting.
- **Long server calls hold the gate.** Query and refresh hold a lease for their duration, as they held the actor in ADR 0001. Moving query to its own connection remains a #352 question.

### 2. The journal lands before the live session

- **A journal PR ([#379](https://github.com/bgarcevic/tomix-cli/issues/379)) comes between #343 and #344.** It adds `TomWriter`, `TomChangeJournal` and `TomObjectIdMap`, and moves every TOM write in the mutation collaborators onto `TomWriter`. The primitives are those of ADR 0001 §4:
  - `Set`: one generic method for every property, for example `w.Set(measure, m => m.Expression, value)` in place of `measure.Expression = value`.
  - `Attach` and `Detach` for collection adds and removes.
  - `Rebind` wherever TOM replaces an instance.

  Most call sites change mechanically. One-shot behaviour does not change: a one-shot session runs the same code with a journal that nobody reads.
- **Two test oracles catch writes that bypass the journal:**
  - For every mutation fixture, the events the journal produces must equal the diff of the snapshots before and after, keyed by object ID.
  - Applying a mutation and then undoing it must leave the serialized TMDL byte-identical to the original.

  A banned-API analyzer may additionally forbid TOM collection `Add` and `Remove` outside `TomWriter`.
- **The journal is the only source of change events.** ADR 0001 §7 stands unchanged. The snapshot diff exists only in tests.
- **#346 shrinks.** It keeps the undo and redo stacks, their limits, the save point and the explicit-transaction surface. The journal itself is already done by then.

### 3. Core contract

#343 adds these to `Tomix.Core/Models`:

| Type | Purpose |
|---|---|
| `ObjectId` | Session-scoped ID, printed `o` plus base-36 (`o1k3`). Serialized as that string, also as a dictionary key. |
| `ModelObject.Id` | The object's ID in a live snapshot. `null` in one-shot snapshots and then left out of JSON, so one-shot contracts do not change. |
| `ModelChange`, `ModelChangeKind`, `ChangeOrigin`, `ChangeOriginKind`, `ModelChangeBatch` | The `model.changed` payload of ADR 0001 §7. |
| `SessionState`, `SessionStateChange` | The lifecycle of ADR 0001 §1. |
| `ILiveModelSession` | State, version, `Changed` and `StateChanged` events, `GetLiveSnapshotAsync`, `LeaseAsync`, `BeginTransactionAsync`, `UndoAsync`, `RedoAsync`. |
| `ILiveSessionLease`, `LiveLeaseOptions` | The lease and transaction described in §1. |
| `LiveModelSnapshot`, `ModelObjectIndex` | A versioned snapshot with lookups from ID to path and from path to ID. |

Undo, redo and explicit transactions are part of the contract now, and implementations may throw `NotSupportedException` until #346.

## Considered and not chosen

- **The actor with one work item per session call (ADR 0001 as written).** A rename and its reference fixups would become separate undo steps, unless handlers open transactions, and that breaks handler session-agnosticism.
- **The actor running the whole request.** This keeps request-level transactions, but needs a custom single-threaded synchronization context under async handlers, and blocks synchronous callers. It is safer than the lease gate only if TOM had thread affinity, and TOM has none.
- **A plain lock that callers take by convention.** It is the same mechanism as the lease gate, but nothing stops code from holding a model reference outside the lock. The lease's capability view closes that gap.
- **Snapshot-diff events in production (#344 before the journal).** It ships sooner and handles cascades for free. But it costs O(model) per commit, misses changes the snapshot does not represent, and is replaced by the journal in #346. It is kept as a test oracle only.
- **Building the journal inside #344.** The result is the same, but it lands as one very large PR that is hard to review.

## Consequences

- **The order of work changes.** It becomes #343 (contracts), then the journal PR (#379), then #344 (`TomLiveModelSession`: lease gate, ID map and journal-driven events, save), then #345 and #346.
- **The gate is a provider-internal primitive.** The FIFO async lock and the lease's capability view live in `Tomix.Provider.Tom`, next to `TomLiveModelSession`. Core holds only the contracts.
- **Long-held leases block other clients.** A slow query or an abandoned explicit transaction holds up everyone else. The idle timeout covers explicit transactions. #352 covers moving query off the gate.
- **The journal PR touches every mutation collaborator.** Its risk is contained by the mechanical shape of the change and by the two oracles, which run on the existing mutation fixtures.
