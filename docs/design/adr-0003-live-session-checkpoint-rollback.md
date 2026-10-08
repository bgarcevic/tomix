# ADR 0003: Checkpoint rollback for the live model session

- **Status:** Proposed
- **Date:** 2026-10-01
- **Issue:** [#379](https://github.com/bgarcevic/tomix-cli/issues/379), part of epic [#341](https://github.com/bgarcevic/tomix-cli/issues/341)
- **Supersedes:** [ADR 0002](adr-0002-live-session-lease-gate-and-journal-first.md) §2 where it makes the journal the source of rollback and undo, and [ADR 0001](adr-0001-live-model-session.md) §4 where undo replays inverse journal entries. The journal stays the only source of change events and of replayable operations.

## Context

ADR 0002 §2 has `TomChangeJournal` record every TOM write so that rollback, undo and change events all come from it. Rolling back means applying each entry's inverse: restore the old value of a `Set`, detach what an `Attach` added, and re-attach what a `Detach` removed. Spikes against TOM 19.117 on the sample models showed that the last of these cannot be done:

| Probe | Result |
|---|---|
| Re-add a removed measure, column or table to its collection | `InvalidOperationException`: an object removed from the model tree cannot be reattached |
| Put a re-created object back at its old position | Not possible. TOM collections have `Add` but no `Insert` |
| `Model.UndoLocalChanges()` on a model loaded from TMDL or `.bim` | Throws for a disconnected model. `HasLocalChanges` stays `false` |
| TOM's own transactions and savepoints (`TxManager`, `TxSavepoint`) | Internal types, reachable only by reflection |
| `Database.Clone()` | TMDL byte-identical to the original on all six sample models. About 0.03 ms per object, a few milliseconds per sample |
| `checkpoint.Model.CopyTo(model)` | Keeps the `Model` and every unchanged object instance. Changed objects come back as new instances appended to their collections, so the content is equal but sibling order can differ |

Undoing a remove by inverse operations would therefore need a clone of the removed object, a rebind of its ID, re-adding every later sibling to restore order, and repointing every relationship, sort-by column, level, perspective entry and translation that referenced the original. That is the most fragile code in the epic, for the operation users rely on most to be exact.

## Decision

### 1. Rollback restores a checkpoint

- **A writing transaction takes a checkpoint.** On its first write, a transaction takes `Database.Clone()` before the write runs. A transaction that only reads takes none. While it takes the clone, the session records the clone's ID for each object by walking the original and the clone side by side, since the two trees are structurally identical.
- **Rollback restores the checkpoint and truncates the journal.** The journal entries of the rolled-back transaction are dropped and no change batch is published.
  - **File-backed sessions (TMDL, `.bim`)** swap the live `Database` for the clone. The result is byte-identical to the state before the transaction.
  - **XMLA sessions** cannot swap, because the `Database` belongs to its `Server`. They restore with `checkpoint.Model.CopyTo(model)`. The result is semantically identical, and sibling order may differ, which a server model does not expose.
- **IDs survive a rollback.** After either restore, every object takes the ID the checkpoint recorded for it, matched by instance where it survived and by path otherwise.
- **Savepoints are nested checkpoints.** A joined lease (ADR 0002 §1) takes its own checkpoint on its first write. Rolling back the joined lease restores that checkpoint and drops only its entries. Committing it discards the checkpoint and keeps its entries in the outer transaction.
- **Swapping is safe because of the lease.** TOM is reachable only through a lease's capability view, so no code outside the session holds a reference to the replaced `Database` or its objects after the lease ends. Inside a lease, collaborators are constructed per lease and take the `Database` from the session.

### 2. The journal records, it does not revert

- `TomWriter` and `TomChangeJournal` stay as ADR 0002 §2 describes. Every TOM write goes through `Set`, `Attach`, `Detach` or `Rebind`.
- The journal remains the only source of change events (ADR 0001 §7) and of the provider-neutral operations that #374 replays onto a reloaded model.
- The journal does not need to invert anything, so its entries do not have to capture enough state to rebuild a removed object.

### 3. Undo and redo use the same checkpoints (#346)

- Each committed transaction keeps its checkpoint as its undo step. Undo restores the checkpoint of the latest step and keeps the state it replaced as the redo step. Redo restores that.
- The undo stack is capped by count, and #346 picks the default. Memory grows with model size times stack depth. #346 may store older steps serialized instead of as live clones if that proves too large.

## Considered and not chosen

- **Inverse operations from the journal (ADR 0002 as written).** It is the cheapest at runtime. It cannot restore order, needs clone-and-rebind for every remove, and needs every reference to a removed object repointed by hand. A missed case corrupts the model silently.
- **Reflection into TOM's internal `TxManager` savepoints.** It is the smallest change, but internal types can change in any package update, and it is not known to work for disconnected models.
- **`Model.CopyTo` for file-backed sessions too.** One restore path for every source would be simpler, but sibling order churns the saved TMDL files, and the rollback oracle requires byte-identical output.
- **Serializing a checkpoint to TMDL or JSON instead of cloning.** It uses less memory, but deserializing is slower than cloning, and it gives the same result. #346 may still use it for older undo steps.

## Consequences

- **Every writing transaction pays for a clone.** It is a few milliseconds on the samples and grows linearly, to roughly 150 ms for a model of 5,000 objects. Reads and read-only leases pay nothing. On large models this exceeds the 50 ms edit budget of ADR 0001 §4. #344 can take the next checkpoint right after a commit, before the next request arrives, and #352 measures whether it is needed. [ADR 0004](adr-0004-live-session-performance-budgets.md) measured about 30–57 ms per clone on a 1,000-measure model, which is the largest single cost of an edit.
- **The rollback oracle of #379 has two forms.** File-backed sources must serialize byte-identical after apply-then-rollback. The `CopyTo` path is checked for equal content with sibling order ignored.
- **The `Database` instance of a file-backed session can change.** Code in the provider must take the `Database` from the session for each lease and never cache it across leases.
- **#346 shrinks again.** Undo and redo are checkpoint stacks rather than journal inversions.
