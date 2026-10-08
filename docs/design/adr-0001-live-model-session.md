# ADR 0001: Live model session

- **Status:** Accepted. §3, the threading parts of §4 and the order of work in Consequences are superseded by [ADR 0002](adr-0002-live-session-lease-gate-and-journal-first.md).
- **Date:** 2026-09-30
- **Issue:** [#342](https://github.com/bgarcevic/tomix-cli/issues/342), part of epic [#341](https://github.com/bgarcevic/tomix-cli/issues/341)
- **Unblocks:** #343 (core abstraction), #344 (TOM live session), #345 (handler refactor), #346 (undo/redo), #348 (protocol), #350 (notifications), #351 (external changes), #370 (change sets), #374 (merge)

## Context

Today every `tx` command is its own process and follows the same steps: resolve the provider, open the model, act, optionally persist, dispose.
`ModelSessionRunner` runs those steps for reads. `MutationRunner` and `MutationLifecycle` run them for writes, including the preview/`--save`/`--stage` modes.
`--stage` keeps edits between invocations by writing a working copy to disk (`StagingStore`). The next command still has to reload the model from that copy.

Epic #341 adds a **live model session**: one model held open in memory that several front ends attach to:

- `tx shell`, a REPL
- `tx serve`, JSON-RPC over stdio
- `tx mcp`, for agents
- `tx ui`, the browser app over WebSocket

A session has to provide:

- responses fast enough for a UI
- stable object identity
- change events
- undo/redo
- reviewable change sets

The existing architecture rules still apply: Core stays BCL-only, App never references a concrete provider, and no TOM types leak out of the providers.

Some facts about the code today shape the decisions below:

1. **`IModelSession` is already a long-lived object.** `TomFileModelSession`, `TmdlModelSession` and `TomServerModelSession` load the TOM `Database` lazily and keep it until they are disposed. The one-shot lifetime comes from the runners, not from the sessions.
2. **Nearly every read goes through `ModelSnapshot`.** `get`, `ls`, `find`, `deps`, `diff`, BPA and save validation all read `GetSnapshotAsync()`, an immutable record tree built by `TomModelSummarizer`. Only query, refresh, deploy and export touch TOM directly.
3. **TOM writes are already centralized** in `TomModelMutator` and its collaborators: `TomObjectAdder`, `TomPropertyApplier`, `TomRemoveCascade`, `TomTextReplacer`, and `TomRefreshPolicyManager` for refresh policies.
4. **TOM refuses to re-attach a removed object.** `MoveObject` works by cloning the object, detaching the original and attaching the clone, then re-creating the perspective memberships and translations that referenced it (`TomModelMutator.MoveObject`). So the object instance does not survive a move, or a remove followed by an undo.
5. **TOM is not thread-safe, and it has no public change notifications.**
6. **Server saves are `Model.SaveChanges()`**, which applies the accumulated local changes in one server transaction. For a `localhost` Power BI Desktop instance, the change stays in memory until the user saves the report in Desktop (`PersistenceKind.LiveModel`).

Epic #341 leaves four questions open, and this ADR answers them: object identity, threading, save for server-backed models, and the scope of change events. It also settles the lifecycle, the undo model, how handlers are shared across front ends, and save semantics for each source.

## Decision

### 1. Session lifecycle

A live session goes through these states:

```
            open                   mutate / undo / redo
 (none) ──────────► Clean ◄──────────────────────────► Dirty
                      │  ▲                               │
                      │  └──────── save (ok) ────────────┤
                      │                                  │
                      │         external change          ▼
                      ├───────────────────────────────► Stale ── reload / keep mine / merge ──► Clean | Dirty
                      │
          close ──────┴──► Closed   (from Dirty: only with save or discard)
```

- **Open.** A provider opens the model once. The session gets a version counter starting at `0`, an empty undo stack and a save point at version `0`.
- **Dirty** means the current undo position is not the save point. It is not "something changed since open". Undoing back to the save point makes the session **Clean** again, as in any editor. Redo past the save point makes it Dirty.
- **Save** persists to the session's source (see §6) and moves the save point to the current position. The undo stack is kept. Undoing after a save makes the session Dirty again, and a further save writes the undone state.
- **Stale** means the source changed underneath the session: a file was edited, the server version moved, or someone edited in Desktop. The session never merges on its own. The client picks one of:
  - **reload**: discard in-memory changes and re-read;
  - **keep mine**: the next save overwrites, which needs `force`;
  - **merge** (#374, after v1): reload the source, then replay the unsaved transactions on top of it, like `git rebase`. Replayed edits that apply cleanly stay separate undo steps. Edits that don't become conflicts in a pending change set:
    - both sides changed the same property;
    - modified here, deleted there;
    - both sides added an object at the same path;
    - references that break after the merge, found by DAX validation.

    The session stays Stale until every conflict is resolved or the merge is aborted.

  Detection is #351. This ADR reserves the state and the three choices, and §4 makes the journal replayable so merge can be added without reworking it. The protocol (#348) reserves `session.merge`.
- **Close.** Closing a Dirty session fails with `TOMIX_SESSION_DIRTY` unless the caller passes `save` or `discard`. A session belongs to its host process (`tx shell`, `tx serve` or `tx ui`). Clients such as the browser and `tx mcp` attach and detach without closing it. The host closes it when its process exits. If it is Dirty at that point, the shell asks first, and non-interactive hosts refuse to exit unless they received `--discard-on-exit`.

**Interaction with `--stage` and one-shot commands.** A live session registers itself in a per-user session registry. #369 needs this registry anyway for discovery: model key, process id, port and token file. The rules are:

- A one-shot mutation (`tx set`, `tx rm`, …) against a model with a live session fails with `TOMIX_SESSION_ACTIVE`. The hint points to the session: `tx shell` or the open `tx ui` tab. Two writers on one model is exactly the conflict the session exists to prevent.
- One-shot reads still work. They read the source on disk or server, so they do not see unsaved session edits, and the warning says so.
- Opening a live session on a model that has staged work fails with `TOMIX_STAGE_PENDING` and the hint `tx stage commit` / `tx stage discard`. The live session does not adopt staged work. Its undo stack and change sets replace staging for interactive use.

### 2. Object identity

- **Session IDs are the stable handle.** Each object gets an opaque ID (`ObjectId`, printed as `o` followed by base-36 digits, e.g. `o1k3`) when the model loads or when the object is created. An ID is unique within the session and never reused, including after the object is removed.
- **Paths stay the external address.** Commands, the shell and agents still type paths. Every result, event and protocol payload carries both `id` and `path`, and a request may address an object by either one. `ModelObject` gets an `Id` field. It is serialized in protocol payloads and left out of today's one-shot JSON contracts, so `GetLsParityTests` and `PropertyCatalogTests` do not change.
- **The mapping lives in the provider.** `TomObjectIdMap` maps TOM `MetadataObject` instances to IDs in both directions. Because TOM swaps instances (fact 4), the map is keyed by instance, and each operation that replaces an instance calls `Rebind(oldInstance, newInstance)` so the replacement keeps the ID:
  - move and rename: `MoveObject`'s clone takes over the original's ID.
  - undo of a remove: the restored clone takes over the removed object's ID. Objects removed in the cascade get their IDs back too, because the journal records the cascade (§4).
  - undo of an add: the ID is retired and never handed out again.
- **IDs do not persist across sessions.** Clients must not store IDs. Anything that outlives a session uses paths, and where TOM has one, the object's `LineageTag`. When a Stale session reloads (#351), IDs are rebound in this order: `LineageTag` first, then path, then a new ID. The reload event lists the objects that got new IDs.

Considered and not chosen:

- **`LineageTag` as the ID.** Relationships, roles, perspectives, annotations, partitions and many other object kinds have no lineage tag. It is optional and editable, and copy-paste can duplicate it.
- **Paths as the ID.** They change on rename and move, and those are exactly the edits a UI must follow (selection, open editors, lineage view).

### 3. Threading model

- **One session actor per live session.** It is a dedicated thread with a single-reader `Channel<SessionWorkItem>`, and it is the only code that touches the TOM `Database`. Every mutation, undo, redo, save, reload, and every read that needs TOM (query, refresh, export, deploy script) is a work item that runs to completion in FIFO order. Each work item carries its client's `CancellationToken`. Cancellation before a work item starts drops it. Cancellation during a mutation rolls back that mutation's transaction (§4).
- **Reads run concurrently on snapshots.** After each committed transaction the actor publishes an immutable `ModelSnapshot` tagged with the new version, built lazily on first request and cached. Snapshot-only reads never enter the queue, and a long-running query cannot block them. These include `get`, `ls`, `find`, `deps`, `diff`, BPA and DAX diagnostics. Each result carries the version it was computed from, so clients can discard results that are out of date.
- **Long server calls such as refresh and query** still run on the actor, because they share the TOM `Server` connection. They report progress through the existing `MutationProgress` and trace sinks. The #352 performance work decides whether query moves to its own connection off the actor. This ADR does not assume it.
- **Snapshot cost** is a known risk: 1,000-measure model, 50 ms budget (#341). The first implementation rebuilds the whole snapshot lazily. #352 measures it, and if needed switches to incremental snapshots (reuse unchanged subtrees, since records are immutable) driven by the change journal. Clients do not need to change. Measured in [ADR 0004](adr-0004-live-session-performance-budgets.md): full rebuilds fit the budget.

### 4. Undo model: a provider-level change journal

Undo uses **inverse operations recorded at the TOM level**, not snapshots of the model and not inverse CLI commands.

- **`TomChangeJournal`.** Every TOM write in `TomModelMutator`'s collaborators goes through a small set of primitives, and each one appends an entry to the open transaction:
  - `SetProperty(obj, member, before, after)`
  - `Attach(parent, collection, obj, index)`
  - `Detach(parent, collection, obj, index)`
  - `Rebind(old, new)`

  Undo replays a transaction's entries backwards and applies each inverse. Redo replays them forwards.
- **Entries are replayable on a different model instance.** Each entry holds the live TOM reference, which undo and redo use. It also records the object's `ObjectId`, its path at the time of the edit, its `LineageTag` where the object kind has one, and the operation in provider-neutral form: the property name and before/after values, or the add or remove request. Merge (#374) uses this to replay unsaved transactions onto a freshly loaded source, resolving each object by `LineageTag` first and then by path. Adding these fields now costs a few fields per entry. Retrofitting them later would mean reworking the journal. A detached object stays in the journal, so undoing a remove re-attaches a clone of it and rebinds its ID (§2). Cascades are just more entries in the same transaction: relationships, hierarchy levels, perspective and translation entries, and M expressions created on add.
- **Transactions are the undo unit.** Each top-level request is one transaction and one undo step, whatever it expands to: one CLI command in the shell, or one RPC call. For example, a rename is the rename plus every `RenameFixup` expression rewrite, and `replace` is all its matches. If a request throws partway, its transaction rolls back, so a failed command leaves the model untouched. One-shot commands do not have this guarantee today. They rely on not saving.
- **Explicit grouping.** `transaction.begin` / `commit` / `rollback`, shown as `begin` / `commit` / `rollback` in the shell, groups several requests into one undo step. Transactions are flat: a nested `begin` is an error. While a transaction is open, only the client that opened it can mutate. Other clients' mutations wait in the queue, and their reads see the last committed version.
- **Change sets (#370) build on the same mechanism.** A proposed change set is a list of requests that has not been applied. Previewing it applies them in a transaction on the actor, captures the snapshot diff and rolls back. Approving it applies them as one transaction, so it undoes as one step. The shell's `--preview` flag does the same preview through the same path.
- **Limits.** The undo stack keeps 500 transactions or about 64 MB of journal, whichever comes first, and drops the oldest. A new mutation after an undo clears the redo stack. Reload clears both stacks. A merge replaces them with the rebased transactions.
- **The save-point snapshot is kept.** The `ModelSnapshot` at the last save point stays in memory. Save validation uses it as its baseline, and merge uses it as the common base for three-way conflict detection.
- **Guarding against direct TOM writes.** A write that bypasses the journal would make undo silently incomplete. The following make that failure visible:
  - Golden tests from #341: for every mutation kind, apply then undo, and the saved TMDL is byte-identical to the original.
  - A test-only check that serializes the database before the operation and after its undo, and compares the two.
  - The rule is written into `Tomix.Provider.Tom/CONTEXT.md`.

Considered and not chosen:

- **A full-model snapshot per operation** (serialize before each write). It is exact and simple, but it costs O(model) time and memory on every edit, which would miss the 50 ms budget on large models. It is kept only as a test oracle.
- **Inverse CLI commands** computed in App (for example, `set` records the old value and undo runs `set` again). This fails on cascades, add-time defaults, rename fixups and instance swaps, all of which live below App.
- **TOM's internal change tracking.** It is not public API, and `UndoLocalChanges` only discards everything since the last sync.

### 5. How the one-shot CLI, `tx shell`, `tx serve`, `tx mcp` and `tx ui` share handlers

- **A session source replaces "open a model" in the runners.** App gets `IModelSessionSource` with one method, `LeaseAsync(ModelReference, CancellationToken) → ModelSessionLease`. `ModelSessionRunner` and `MutationRunner` take a session from the source instead of calling `provider.OpenAsync` themselves.
  - `OneShotSessionSource` keeps today's behavior: resolve the provider, open, and dispose when the lease ends.
  - `LiveSessionSource` hands out the already-open live session. Ending the lease does not dispose it.

  Handlers keep their request and result types. #345 makes this change mechanically.
- **Mutation modes gain `Live`.** Under a live source, `MutationLifecycle.BeginAsync` resolves to `MutationMode.Live`, and a mutation runs in these steps:
  1. Apply inside a transaction.
  2. Commit.
  3. Mark the session Dirty and emit events.

  It does not persist. The other modes behave as follows in the shell:

  | Mode | Behaviour under a live source |
  |---|---|
  | `--save` | Apply, then save. Two steps in one request, still one undo step. |
  | `--preview` | Apply, diff and roll back (§4). |
  | `--stage` / `--revert` | Rejected with `TOMIX_SESSION_STAGE_UNSUPPORTED`. |

  Save validation (`SaveValidation`) keeps its baseline from the last save point instead of from open.
- **Front ends only parse and render.**
  - `tx shell` reuses the `System.CommandLine` tree. The model argument defaults to the session's model, and output goes through the same renderers.
  - `tx serve`, `tx mcp` and `tx ui` do not parse CLI strings. They deserialize JSON into the same `*Request` records and call the same handlers. Results are serialized with `AppJsonContext`, so a protocol result looks exactly like the command's `--output-format json` payload. #348 locks that down with approved-snapshot tests.
- **Where it lives.**

  | Project | New responsibility |
  |---|---|
  | `Tomix.Core` | `ILiveModelSession`, `ObjectId`, `ModelChange` and `ModelChangeBatch`, `SessionState`, the transaction contract. BCL-only. |
  | `Tomix.Provider.Tom` | `TomLiveModelSession` (actor, journal, ID map), used for TMDL, `.bim` and XMLA sources. `Tomix.Provider.Tmdl` reuses it, since TMDL loads into the same TOM `Database`. |
  | `Tomix.App` | `IModelSessionSource` and its two implementations, `SessionHost` (registry, attach and detach, approval policy from #370), the `Live` mutation mode. |
  | `Tomix.Cli` | The `shell`, `serve`, `mcp` and `ui` commands and their transports. No model logic. |

### 6. Save semantics per source

| Source | Save means | After save |
|---|---|---|
| TMDL folder (including a PBIP `definition` folder) | `TomModelExporter` plus `TmdlFolderSync` into the source folder: write only files whose content changed, keep EOL and BOM, delete stale `.tmdl`. Same code path as one-shot `--save`. | Clean. Undo stays available and makes the session Dirty. The file hash and mtime at the save point are recorded for Stale detection. |
| `.bim` / `.tmsl` | Rewrite the file in its inferred serialization (`InPlaceSerializationGuard`). | As above. |
| XMLA (Power BI service, Fabric, Azure AS, SSAS) | `Model.SaveChanges()`, one server transaction carrying all local changes since the last save. `XmlaResults` errors fail the save and leave the session Dirty with nothing applied. Configured workspace-mirror sync runs afterwards, as today. | Clean. The server has no undo: undoing after a save changes the in-memory model, which is Dirty until the next save sends the reverse edits. The UI must say "saved to server". Before `SaveChanges`, the session checks the database's last-update timestamp against the value recorded at open or last save. If it moved, the session is Stale and the save needs `force` (#351). |
| Power BI Desktop (`localhost`) | `Model.SaveChanges()` to the Desktop instance, the same as XMLA. | Reported as `PersistenceKind.LiveModel`: the change is live in Desktop but reaches the `.pbix`/`.pbip` only when the user saves the report in Desktop. Edits made in Desktop mark the session Stale. |

- "Save to somewhere else" is a separate operation: `export`, or `save --to <path>`. It never moves the save point or changes where the session saves to.
- Save runs on the actor as a normal work item. It is not a transaction and not an undo step.
- A merge (#374) is one exclusive actor work item. For server sources it ends with the session holding the server's current model plus the rebased local edits, so the next `SaveChanges()` sends only those edits.

### 7. Event model and granularity

- **One event per committed transaction, listing objects.** The event also names the properties that changed but carries no values:

  ```json
  {
    "method": "model.changed",
    "params": {
      "version": 42,
      "transaction": "t17",
      "origin": { "client": "mcp-1", "kind": "apply" },
      "changes": [
        { "id": "o1k3", "path": "Sales/Sales Amount", "oldPath": "Sales/Total Sales", "change": "renamed" },
        { "id": "o1k9", "path": "Sales/Margin %", "change": "modified", "properties": ["Expression"] }
      ]
    }
  }
  ```

  - `change` is one of `added`, `removed`, `modified`, `renamed` or `moved`.
  - `origin.kind` is one of `apply`, `undo`, `redo`, `reload` or `changeSet`.

  Clients fetch values with `get`, by ID or path, at that `version`. Events stay small and have one shape whether a transaction touched 1 object or 400.
- **The journal produces the events.** The same entries that make undo work also feed the event builder, so undo and redo events are exact inverses with no extra bookkeeping.
- **`version` is monotonic and gap-free.** A client that sees a gap, or reconnects, calls `session.snapshot` to resync instead of replaying events.
- **Session events** report state transitions: `session.state` (`clean`, `dirty`, `saving`, `stale`, `closed`), `session.saved`, and `transaction.opened` / `closed` for explicit groups.
- **Derived data is recomputed in full with a debounce, not incrementally.** 250 ms after the last committed transaction, the host recomputes the dependency graph, DAX diagnostics and BPA from the published snapshot, off the actor. It then emits `diagnostics.updated { version }`. Incremental updates are an optimization #352 may add behind the same event.

## Answers to the open questions in #341

| Question | Decision | Section |
|---|---|---|
| Object identity | Session-scoped opaque IDs, rebound explicitly when TOM swaps instances. Paths stay the external address. `LineageTag` is used only to rebind after a reload. | §2 |
| Threading | One actor per session owns TOM. Reads run concurrently on versioned, immutable snapshots. | §3 |
| Server-backed "save" | `Model.SaveChanges()` to the server or Desktop instance. Undo after a save is local and needs another save. Local export is a separate operation. | §6 |
| Scope of change events | One event per transaction, per object, naming properties without values. Derived data is recomputed in full with a 250 ms debounce. | §7 |

## Consequences

- **Refactors this requires:**
  - #343: add the Core contracts.
  - #344: route every TOM write in the mutator collaborators through `TomChangeJournal`. This is the largest single change.
  - #345: replace `provider.OpenAsync` in the two runners with `IModelSessionSource`.
- **One-shot behaviour does not change.** The refactor keeps `CommandSurface.approved.txt` and the JSON contract tests green. The only visible addition is `TOMIX_SESSION_ACTIVE` when a live session holds the model.
- **New diagnostics** to add to `docs/error-codes.md`: `TOMIX_SESSION_ACTIVE`, `TOMIX_SESSION_DIRTY`, `TOMIX_SESSION_STAGE_UNSUPPORTED`, `TOMIX_STAGE_PENDING`.
- **`--stage` stays for one-shot and CI use.** Interactive work moves to the live session, and the editing guide should say so once `tx shell` ships.
- **Risks carried forward:**
  - Snapshot rebuild cost (#352).
  - Journal completeness (golden tests).
  - Desktop's partial support for some write operations over `localhost`. This already exists for one-shot `--save` and is not made worse here.
- **Merging external changes is deferred to #374**, but it is designed for here: a Stale session offers three choices, journal entries can be replayed on a freshly loaded model, and the save-point snapshot serves as the merge base. v1 ships reload and keep mine.

## Status of this document

This ADR is **Proposed** until the PR that adds it merges, and then **Accepted**. Later changes to these decisions go in a new ADR that supersedes the relevant section, not in edits to this one.
