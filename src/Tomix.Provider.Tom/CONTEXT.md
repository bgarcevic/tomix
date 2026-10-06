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

**A live session on files watches them** (#351). `TomModelSource` gives each source a
`Fingerprint` (for files, `SourceFingerprint`: a hash of the `.bim`, or of a folder's `.tmdl` files by
relative path and content) and a `Watch`. `TomLiveModelSession` keeps the fingerprint it last
opened, reloaded or saved; a watcher event checks it after `SourceSettleDelay`, and an in-place
save checks it first and throws `ModelSourceChangedException` rather than overwrite. Checks and
saves share `_sourceLock`, so a check never reads files a save is writing. `KeepChanges` takes the
files as they are as the new baseline (`save --force`); `ReloadAsync` loads them again through
`TomChangeJournal.Reload`, which keeps the ID of every object whose kind and path survive.
Server sources have no fingerprint yet, so they never turn stale.

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
