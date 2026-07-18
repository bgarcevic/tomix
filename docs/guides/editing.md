# Editing & staging

The mutation commands — `add`, `set`, `mv`, `rm`, `replace`, `format`,
`script` — share one lifecycle. By default they **preview**: the command runs
against the in-memory model and shows you the result, but nothing is written.

You choose what happens next with one of three flags:

| Flag | Effect |
|------|--------|
| *(none)* | Preview only. Nothing is persisted. |
| `--save` | Persist the mutation to the source location immediately. |
| `--stage` | Record the mutation in the session's staging area. |
| `--save-to <path>` | Persist to a different path, leaving the source untouched (implies `--save`). |

## Staging

Staging lets you build up a batch of edits and commit (or abandon) them as a
unit — useful when a change only makes sense as a whole:

```sh
tx set "Sales[Total Sales]" -q "SUM(Sales[Amount])" --stage
tx mv "Sales/Old Name" "Sales/New Name" --stage
tx rm "Sales/Obsolete" --stage

tx stage            # show staged mutations for the active model
tx stage list       # all staged models in this session
tx stage commit     # promote staged mutations onto the source (and workspace mirror)
tx stage discard    # throw them away
```

An individual staged mutation can be undone with `--revert` on the command
that created it.

## Renames rewrite DAX

When you rename an object (`mv`, or `set` on `Name`), `tx` rewrites DAX
expressions that reference the old name automatically. References that cannot
be rewritten (role RLS filter expressions) produce a warning listing the
objects left broken.

- `--strict-refs` — fail instead of warning when a rename leaves references
  broken.
- `--no-fix-refs` — don't rewrite anything; warn about every stale reference.

## Removals are guarded

`rm` refuses to remove an object that is still referenced by DAX, and lists
the referencing objects. `--force` removes it anyway and reports the
now-broken references. Structural references — relationships, sort-by
columns, hierarchy levels, perspective entries, role permissions — never
block; they are cascade-removed with the object.

```sh
tx rm "Sales/Amount" --dry-run     # see what would happen
tx rm "Sales/Amount" --force
```

## Validation on save

Saves validate the resulting model; a mutation that introduces DAX validation
errors is rejected unless you pass `--force`. Exit codes and diagnostic codes
for the whole lifecycle are listed in the
[error codes reference](../error-codes.md).

## Bulk edits

`replace` applies a find-and-replace across the model (`--dry-run` to
preview), `format` reformats DAX and M expressions, and `script` runs C#
against the TOM model for anything the built-in commands don't cover:

```sh
tx replace "[OrderDate]" "[ShipDate]" --dry-run
tx format --save
tx script transform.csx --save
```
