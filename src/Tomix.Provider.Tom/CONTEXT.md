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

## Live session (planned)

[ADR 0001](../../docs/design/adr-0001-live-model-session.md) adds `TomLiveModelSession`, used for
TMDL, `.bim` and XMLA sources (`Tomix.Provider.Tmdl` reuses it):

- One actor thread owns the TOM `Database`. No other thread touches TOM. Snapshot reads are
  served from an immutable, versioned `ModelSnapshot`.
- `TomChangeJournal` records every TOM write as a primitive (`SetProperty`, `Attach`, `Detach`,
  `Rebind`). Undo, redo, rollback and change events all come from it. **Every TOM write in the
  mutator collaborators must go through the journal**. A direct write leaves undo silently
  incomplete, and the apply-then-undo golden tests are there to catch it.
  Entries also carry the object's ID, path and `LineageTag` plus a provider-neutral form of the
  operation, so unsaved transactions can be replayed onto a reloaded model (merge, #374).
- `TomObjectIdMap` maps TOM instances to session `ObjectId`s. TOM cannot re-attach a removed
  object, so any operation that replaces an instance (move, undo of a remove) must `Rebind` the
  new instance to the old ID.

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
