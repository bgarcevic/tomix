# Model tests

DAX regression tests for `tx test`. Each test is a `.dax` file holding one `EVALUATE`
query. Run them against a deployed model or a local instance:

```sh
tx test ./tests -s <workspace> -d <model>
```

## How a test is decided

- **Snapshot** (the default): the result is compared with a sibling `<name>.expected.json`.
  A test without one fails as `MISS` until you record it:

  ```sh
  tx test ./tests --update-missing   # record snapshots for new tests
  tx test ./tests --update           # accept intended changes, then review the git diff
  ```

- **Assertions**: `tx:` comment lines before the query check the result instead, so the
  test needs no snapshot and survives data refreshes:

  ```dax
  // tx: rows>=1
  // tx: columns=Sales[Region], [Total Sales]
  EVALUATE SUMMARIZECOLUMNS ( Sales[Region], "Total Sales", [Total Sales] )
  ```

  Add `snapshot` to keep a snapshot as well.

- **Expected error**: `// tx: expect-error=cannot be found` passes only when the query fails
  with a matching error.

## Directives

| Directive | Effect |
|-----------|--------|
| `tolerance=0.01` / `tolerance=0.1%` | Accept numeric differences this small. |
| `ignore-row-order` | Compare rows in any order. |
| `rows=<n>`, `rows>=<n>`, `rows<=<n>` | Assert the row count. |
| `columns=<names>` | Assert these columns are in the result. |
| `snapshot` | Keep a snapshot alongside the assertions. |
| `expect-error[=<regex>]` | The query must fail. |
| `tags=smoke,nightly` | Tag the test; run tagged tests with `--tag smoke`. |

`tx test list ./tests` shows every test, its tags and how it is decided.

## CI

```sh
tx test ./tests -s <workspace> -d <model> --ci github --trx results.trx
```

The run exits `1` on any failing test, so the pipeline blocks the merge. Use `--ci vsts` on
Azure Pipelines.
