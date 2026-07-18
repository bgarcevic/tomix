# Modify

Commands that change the model. They share the mutation lifecycle described
in [Editing & staging](../guides/editing.md): **preview by default**, persist
with `--save`, batch with `--stage`, or write elsewhere with
`--save-to <path>` (which implies `--save`). `--serialization tmdl|bim`
controls the on-disk format, `--force` saves past validation errors, and
`--no-sync` skips the workspace mirror.

Those shared lifecycle options are not repeated in the tables below.

## `add` — add an object

```
tx add <path> [model] [options]
```

The path names the new object (`Sales/Revenue`, `'Sales'[Revenue]`);
relationships use `Sales[Key]->Product[Key]` (many side → one side).

| Option | Description |
|--------|-------------|
| `-t, --type <type>` | Object type: `Table`, `CalcTable`, `CalcGroup`, `Measure`, `CalcColumn`, `DataColumn`, `Hierarchy`, `Level`, `Calendar`, `CalcItem`, `KPI`, `Partition`, `MPartition`, `EntityPartition`, `PolicyRangePartition`, `Expression`, `Function`, `Perspective`, `Culture`, `ProviderDataSource`, `StructuredDataSource`, `Role`, `TablePermission`, `Member`, `Relationship`. Often inferred from a container keyword in the path; data sources always require `-t`. |
| `-i <value>` | Expression or value for the new object. `-` reads from stdin. |
| `-q <property>` | Extra property to set on the new object; pair each `-q` with a following `-i`. Repeatable. |
| `--file <file>` | Read the expression from a file. |
| `--columns <names>` | Comma-separated columns to create on a new table (Table type only). |
| `--if-not-exists` | Succeed silently if the object already exists (exit 0). |
| `--mode <mode>` | Partition storage mode: `Import`, `DirectQuery`, `Dual`, `DirectLake`, `Push`, `Default`. |

??? note "Data-source and partition options"

    | Option | Description |
    |--------|-------------|
    | `--source <provider>` | Provider name for a ProviderDataSource (e.g. `System.Data.SqlClient`). |
    | `--source-type <type>` | Connection protocol for a StructuredDataSource (e.g. `tds`). |
    | `--endpoint <address>` | Server/endpoint address for a data source connection. |
    | `--connection-string <cs>` | Full connection string for a ProviderDataSource. |
    | `--source-database <db>` | Source database for a data source connection. |
    | `--source-table <table>` | Source entity/table name for an EntityPartition. |
    | `--source-schema <schema>` | Source schema for an EntityPartition. |
    | `--partition-expression <expr>` | M/DAX expression for a partition source. |
    | `--range-start / --range-end <yyyy-MM-dd>` | Refresh-policy range for a PolicyRangePartition. |
    | `--range-granularity <g>` | `Day` (default), `Month`, `Quarter`, `Year`. |

```sh
tx add "Sales/Revenue" -t Measure -i "CALCULATE(SUM(Sales[Amount]))" --save
tx add tables/Sales/measures/Revenue -i - < expression.dax
tx add "Sales/Revenue" -i "SUM(Sales[Amt])" -q formatString -i "$#,0"
```

## `set` — set a property

```
tx set <path> [model] [options]
```

| Option | Description |
|--------|-------------|
| `-q <property>` | Property expression. Accepts dotted paths, bracket indexers, and DisplayName matching. |
| `-i <value>` | Value for the preceding `-q`. `-` reads from stdin. |
| `-t, --type <type>` | Disambiguate when the path matches multiple objects. |
| `--strict-refs` | Fail when a rename leaves DAX references broken. |
| `--no-fix-refs` | Do not rewrite DAX references to a renamed object; warn instead. |

```sh
tx set "Sales[Total Sales]" -q "CALCULATE(SUM(Sales[Amount]))"    # expression is the default property
tx set tables/Sales/Name -i "Sales_v2" --save
```

## `mv` — move or rename

```
tx mv <source> <destination> [model] [options]
```

Renames rewrite referencing DAX automatically; `--strict-refs` and
`--no-fix-refs` behave as on `set`.

```sh
tx mv "Sales/Old Name" "Sales/New Name" --save
tx mv tables/Sales tables/SalesData
```

## `rm` — remove an object

```
tx rm <path> [model] [options]
```

| Option | Description |
|--------|-------------|
| `--dry-run` | Show what would be removed without saving. |
| `--force` | Remove even if the object has DAX dependents (reports the now-broken references). |
| `--if-exists` | Succeed silently if the object does not exist. |
| `-t, --type <type>` | Disambiguate when the path matches multiple table-children. |

Removal is blocked while DAX still references the object; structural
references (relationships, sort-by, hierarchy levels, perspectives, role
permissions) cascade-remove instead.

```sh
tx rm "Sales/Obsolete" --dry-run
tx rm tables/Staging --save
```

## `replace` — find and replace

```
tx replace [pattern] [replacement] [model] [options]
```

| Option | Description |
|--------|-------------|
| `--in <scope>` | `names`, `expressions`, `descriptions`, `displayFolders`, `formatStrings`, `annotations`, `all` (default; excludes annotations). |
| `--regex` | Treat the pattern as a regular expression. |
| `--case-sensitive` | Case-sensitive matching. |
| `--dry-run` | Preview changes without applying. |

```sh
tx replace "[OrderDate]" "[ShipDate]" --dry-run
tx replace "old_name" "new_name" --in names --save
```

## `format` — format DAX and M

```
tx format [model] [options]
```

Uses the DAX and Power Query formatter APIs. With no target, formats every
expression in the model.

| Option | Description |
|--------|-------------|
| `-e, --expression <expr>` | Format an inline expression (no model needed). |
| `-p, --path <path>` | Format the expression on one object. |
| `--lang <dax\|m>` | Expression language. |
| `--semicolons` | Use semicolons as DAX list separators. |
| `--long` | Prefer long-line formatting. |
| `--no-space-after-function` | No space between a DAX function name and `(`. |

```sh
tx format -e "CALCULATE(sum(sales[amt]))"
tx format -p "Sales[Total Sales]" --save
tx format --save                     # whole model
```

## `script` — run C# against the model

```
tx script [model] [options]
```

Scripts get a `Model` variable (the TOM model) — the escape hatch for
anything the built-in commands don't cover.

| Option | Description |
|--------|-------------|
| `-S, --script <file>` | Path(s) to `.cs`/`.csx` script file(s). Repeatable. |
| `-e, --expression <code>` | Inline C# expression(s). `-` reads from stdin. |
| `--dry-run` | Compile and report errors without executing. |

```sh
tx script -e "Model.Tables.Count"
tx script transform.csx --save
```

## `incremental-refresh` — refresh policies

```
tx incremental-refresh <show|set|rm|apply> <table> [options]
```

| Subcommand | Description |
|------------|-------------|
| `incremental-refresh show <table>` | Show the incremental refresh policy. |
| `incremental-refresh set <table>` | Create or edit the policy on a table. |
| `incremental-refresh rm <table>` | Remove the policy from a table. |
| `incremental-refresh apply <table>` | Apply the policy on a deployed model (generates partitions server-side). |

```sh
tx incremental-refresh show Sales
tx incremental-refresh set Sales --rolling-window-periods 10 --rolling-window-granularity year \
  --incremental-periods 3 --incremental-granularity day --source-expression-file source.m --save
tx incremental-refresh apply Sales --no-refresh
```
