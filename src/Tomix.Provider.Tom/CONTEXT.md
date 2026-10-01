# Tomix.Provider.Tom

Adapter around Microsoft Tabular Object Model.

## Responsibilities

- Translate TOM objects into tomix core abstractions.
- Centralize TOM-specific logic.
- Hide TOM implementation details from the rest of the codebase.
- Resolve the model display name shown by every command (`ModelDisplayName`):
  TOM database name → sibling `.platform` displayName → file/folder name
  (a PBIP `definition` folder inherits its item root's name) → caller fallback
  → `(unnamed)`.
- Save TMDL without churn (`TomModelExporter` + `TmdlFolderSync`): serialize to a
  staging folder, then write only files whose content changed (ignoring EOLs and
  trailing newlines), keep each rewritten file's EOL/BOM, match each M partition's
  existing `source =` depth, and delete only stale `.tmdl` files.

## Mutation structure

`TomModelMutator` is the public facade all sessions construct (`new TomModelMutator(database)`); it delegates to internal collaborators:

- `TomObjectAdder` — the `AddObject` type dispatch and per-type builders, plus add-option validation.
- `TomMutationTargetResolver` — path → object resolution (DAX forms, slash paths, container keywords, relationship endpoints); defines `TomResolvedObject`.
- `TomPropertyApplier` — per-type property assignment, annotation handling, expression edits, and value parsers.
- `TomTextReplacer` — model-wide text find/replace with previews.
- `TomMutationPaths` — shared path/name/type normalization and the mutation-path regexes.
- `TomRemoveCascade` — cascade collection for removals (remove dispatch stays on the facade).

**Every TOM write in these collaborators goes through `TomWriter`**: `w.Set(obj, o => o.Prop, value)`
for properties, `Attach`/`Detach` for collection adds and removes, and `Rebind` before attaching a
replacement instance (TOM cannot re-attach a removed object, so moves and role-member edits swap
instances). Objects still being built and not yet attached may be written directly. The public
constructor uses `TomWriter.Untracked`, so one-shot sessions record nothing; a live session passes
its journal's writer through the internal constructor. `BannedSymbols.txt` rejects direct TOM
collection `Add`/`Remove` at build time.

## Change journal and live session

[ADR 0001](../../docs/design/adr-0001-live-model-session.md),
[ADR 0002](../../docs/design/adr-0002-live-session-lease-gate-and-journal-first.md) and
[ADR 0003](../../docs/design/adr-0003-live-session-checkpoint-rollback.md) describe the design.

In place (#379):

- `TomChangeJournal` records every write made through its `Writer` and groups writes into
  transactions; nested ones are savepoints. Commit folds the entries into `ModelChange`s
  (`TomChangeEvents`), the only source of change events. Entries also carry the object's ID, path
  and `LineageTag` plus the property and before/after values as text, so unsaved transactions can
  be replayed onto a reloaded model (merge, #374).
- Rollback restores a checkpoint, not inverse entries: a writing transaction takes
  `Database.Clone()` on its first write. File-backed sessions swap the clone in (byte-identical);
  databases owned by a `Server` restore with `Model.CopyTo` (same content, sibling order may
  differ). The `Database` instance can change, so never cache it across leases.
- `TomObjectIdMap` maps TOM instances to session `ObjectId`s; `TomObjectTree` defines which objects
  are tracked and their paths, matching `TomModelSummarizer`, which stamps IDs when given the map.
- `TomChangeJournalOracleTests` runs every mutation kind through two oracles: events equal the
  ID-keyed snapshot diff, and apply-then-rollback restores the model. A new mutation path gets a
  row there.

Planned (#344): `TomLiveModelSession`, used for TMDL, `.bim` and XMLA sources
(`Tomix.Provider.Tmdl` reuses it). A FIFO lease gate serializes all access to the TOM `Database`;
TOM is reachable only through a lease's capability view, which dies with the lease; a lease is a
journal transaction (commit publishes, dispose without commit rolls back). Snapshot reads are
served from an immutable, versioned `LiveModelSnapshot`.

## Cross-folder dependencies

- Depends on `/src/Tomix.Core`.
- May be used by `/src/Tomix.Provider.Tmdl`.
- Must not depend on `/src/Tomix.Cli`.
- Must not leak TOM types into `/src/Tomix.Core` or `/src/Tomix.App`.

## Rules

- Return `Tomix.Core` types from public APIs.
- Keep mapping code explicit and tested.
- Treat provider behavior as infrastructure, not domain logic.

## Test

```bash
dotnet test tests/Tomix.Provider.Tom.Tests
```
