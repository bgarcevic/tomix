# Modify

Commands that change the model. They share the mutation lifecycle described
in [Editing & staging](../guides/editing.md): **preview by default**, persist
with `--save`, batch with `--stage`, or write elsewhere with
`--save-to <path>` (which implies `--save`). `--serialization tmdl|bim`
controls the on-disk format, `--force` (alias `-f`) saves despite newly
introduced validation errors, `--overwrite` lets `--save-to` replace an existing target, and
`--no-sync` skips the workspace mirror. `--dry-run` previews: the change is
applied to the in-memory model and rendered, but nothing is written.

Before `--save` or `--save-to` writes anything, tx runs the same validation as
`tx validate`. Only errors introduced by this command block the save; existing
errors and warnings do not. A blocked save exits 1 and lists the new errors.
`--force` writes anyway and reports the introduced errors. `rm --force` also
bypasses its dependent-reference guard. Set `validateOnSave` to `false` with
`tx config set validateOnSave false` to disable this gate; it is on by default.
Staged edits are checked when you run `tx stage commit`. The `--force` flags on
`init`, `connect`, `deploy`, and `config init` retain their command-specific uses.

A TMDL save rewrites only the files whose content changed, so a small edit gives
a small git diff. Untouched files keep their bytes, line endings (CRLF checkouts
stay CRLF), and M partition indentation. Stale `.tmdl` files for removed or
renamed objects are deleted. Other files in the folder, such as a README, are
left alone.

Those shared lifecycle options are not repeated in the tables below.

### Saving to Power BI Desktop

When the active connection is a running Power BI Desktop model (`localhost:<port>`),
`--save` writes the change to the model Desktop has open in memory. The change is live
straight away (a fresh `tx get` or `tx query` sees it), but Desktop keeps it only until the
report closes. **Save the report in Power BI Desktop to keep the change.** tx prints this
reminder on stderr after every save to Desktop, and JSON output reports
`"persistence": "liveModel"`.

### JSON result

Every mutation command (`add`, `mv`, `set`, `rm`, `replace`, `format`, `save`,
`vertipaq --annotate`, `bpa run --fix`, `bpa rules ignore`) reports the same
persistence fields under `data`:

| Field | Type | Meaning |
|-------|------|---------|
| `status` | string | `saved`, `staged`, `preview` (applied in memory only), `dryRun`, `unchanged` (nothing to change), or `reverted` |
| `dryRun` | bool | `--dry-run` was passed |
| `saved` | bool | The change was persisted. Always a bool |
| `savedTo` | string | Where it was saved: a folder or file path, or `server / database`. Present only when saved |
| `persistence` | string | `file`, `liveModel` (Power BI Desktop, in memory until the report is saved), or `service`. Present only when saved |
| `target` | object | `{server, database, model}` for a remote or Desktop model. `model` is the friendly name `tx connect` shows |
| `sync` | object | Workspace mirror sync: `{status, target?, warning?}`. `status` is `notAttempted`, `notConfigured`, `skipped`, `succeeded`, or `failed` |
| `newValidationErrors` | int | Errors the change introduced, when the save gate measured them |

The object path uses a past-tense key (`added`, `moved`, `removed`, `set`) only when the
change was saved or staged. Previews and dry runs use `wouldAdd`, `wouldMove`,
`wouldRemove`, or `wouldSet`, so a script never reads a preview as done:

```json
{
  "data": {
    "wouldRemove": "Sales/Total Sales",
    "status": "dryRun",
    "dryRun": true,
    "saved": false,
    "sync": { "status": "notAttempted" }
  },
  "diagnostics": []
}
```

A save to Power BI Desktop:

```json
{
  "data": {
    "added": "Sales/Margin",
    "status": "saved",
    "dryRun": false,
    "saved": true,
    "savedTo": "localhost:51234 / 0f1e2d3c-...",
    "persistence": "liveModel",
    "target": { "server": "localhost:51234", "database": "0f1e2d3c-...", "model": "Sales Report" },
    "sync": { "status": "notConfigured" }
  },
  "diagnostics": []
}
```

A failed or skipped sync includes a `warning`. A failed sync exits 1 even though the save
succeeded.

## `add` — add an object

```
tx add <path> [model] [options]
```

The path names the new object (`Sales/Revenue`, `'Sales'[Revenue]`);
relationships use `Sales[Key]->Product[Key]` (many side → one side).

| Option | Description |
|--------|-------------|
| `-t, --type <type>` | Object type: `Table`, `CalcTable`, `CalcGroup`, `Measure`, `CalcColumn`, `DataColumn`, `Hierarchy`, `Level`, `Calendar`, `CalcItem`, `KPI`, `Partition`, `MPartition`, `EntityPartition`, `PolicyRangePartition`, `Expression`, `Function`, `Perspective`, `Culture`, `ProviderDataSource`, `StructuredDataSource`, `Role`, `TablePermission`, `Member`, `Relationship`. Often inferred from a container keyword in the path; data sources always require `-t`. |
| `--expression <value>` (`-e`) | Expression or value for the new object. `-` reads from stdin. |
| `--set <name=value>` | Set a property on the new object, e.g. `--set formatString="#,0"`. Repeatable; `name=-` reads the value from stdin. |
| `--file <file>` | Read the expression from a file. |
| `--columns <names>` | Comma-separated columns to create on a new table (Table type only). |
| `--if-not-exists` | Do nothing and exit 0 when the object already exists. |
| `--mode <mode>` | Storage mode for the partition: `Import`, `DirectQuery`, `Dual`, `DirectLake`, `Push`, `Default`. |

??? note "Data-source and partition options"

    | Option | Description |
    |--------|-------------|
    | `--source <provider>` | Provider name for a ProviderDataSource (e.g. `System.Data.SqlClient`). |
    | `--source-type <type>` | Connection protocol for a StructuredDataSource (e.g. `tds`). |
    | `--endpoint <address>` | Server/endpoint address for a data source connection. |
    | `--connection-string <cs>` | The connection string used by a ProviderDataSource. |
    | `--source-database <db>` | Source database for a data source connection. |
    | `--source-table <table>` | Source entity/table name for an EntityPartition. |
    | `--source-schema <schema>` | Source schema for an EntityPartition. |
    | `--partition-expression <expr>` | The M or DAX expression defining the partition's source. |
    | `--range-start / --range-end <yyyy-MM-dd>` | Refresh-policy range for a PolicyRangePartition. |
    | `--range-granularity <g>` | `Day` (default), `Month`, `Quarter`, `Year`. |

```sh
tx add "Sales/Revenue" -t Measure --expression "CALCULATE(SUM(Sales[Amount]))" --save
tx add tables/Sales/measures/Revenue --expression - < expression.dax
tx add "Sales/Revenue" -e "SUM(Sales[Amt])" --set formatString="$#,0"
```

## `set` — set a property

```
tx set <path> [model] [options]
```

| Option | Description |
|--------|-------------|
| `--set <name=value>` / `-p <name=value>` | Property assignment, e.g. `--set expression="SUM(Sales[Amount])"`. Repeatable; all assignments are applied together. `name=-` reads the value from stdin. |
| `-t, --type <type>` | Disambiguate when the path matches multiple objects. |
| `--strict-refs` | Fail when a rename leaves DAX references broken. |
| `--no-fix-refs` | Do not rewrite DAX references to a renamed object; warn instead. |

```sh
tx set "Sales[Total Sales]" --set expression="CALCULATE(SUM(Sales[Amount]))"
tx set "Sales[Total Sales]" --set formatString="#,0" --set displayFolder=KPIs --save   # one load, one save
tx set Sales --set name=Sales_v2 --save
tx set tables/Sales --set excludeFromModelRefresh=true
tx set "Sales[Amount]" --set summarizeBy=Sum
tx set "Dates[Month]" --set sortByColumn=MonthNo                  # empty value clears it
tx set "Sales[OrderId]" --set isKey=true
tx set "Sales[Total Sales]" -t kpi --set statusGraphic="Cylinder"
tx set "Sales Territory/'Sales Territories'" --set hideMembers=HideBlankMembers
tx set "Sales Territory/'Sales Territories'/Region" --set ordinal=2
tx set Sales/Sales -t partition --set mode=DirectQuery
```

Translations use `translation:<culture>/<property>`, where the property is
`caption` (alias `name`), `description`, or `displayFolder` (measures,
columns, and hierarchies only). Tables, columns, measures, hierarchies,
levels, and the model root (`.`) can be translated. The culture must exist
already; add it with `tx add Cultures/<culture> -t Culture`. An empty value
removes the translation, and `tx get` reads translations back under the same key.

```sh
tx add Cultures/da-DK -t Culture --save
tx set "Sales[Total Sales]" --set translation:da-DK/caption="Omsætning" --set translation:da-DK/displayFolder="Nøgletal" --save
tx set "Sales[Total Sales]" --set translation:da-DK/caption= --save   # remove it
```

When the edited property carries DAX (for example a measure's `expression`),
text output previews the change with `Before:`/`After:` lines and syntax
colors. Non-DAX properties, unchanged values, and `--output-format json`
show no preview.

Columns accept every writable scalar property (`sourceColumn`, `dataType`,
`dataCategory`, `summarizeBy`, `sortByColumn`, `isKey`, `isNullable`,
`isUnique`, `isAvailableInMDX`, `keepUniqueRows`, `encodingHint`, lineage
tags, and more) — everything `tx get` shows for the column. An unsupported
property name lists the full writable set; enum-valued properties list
their valid values on a bad value.

Tables accept every writable scalar property too (`isPrivate`,
`excludeFromModelRefresh`, `excludeFromAutomaticAggregations`,
`alternateSourcePrecedence`, `showAsVariationsOnly`, `systemManaged`,
`directLakeIndexingBehavior`, `lineageTag`, `sourceLineageTag`) —
everything `tx get` shows for the table.

Measures accept every writable scalar property (`dataCategory`,
`isSimpleMeasure`, `lineageTag`, `sourceLineageTag`, and more). KPIs
accept `description`, `targetExpression`, `statusExpression`,
`trendExpression`, `targetFormatString`, `statusGraphic`, `trendGraphic`,
`statusDescription`, `targetDescription`, and `trendDescription` —
address them with `-t kpi` or a `/KPI` path suffix.

Hierarchies accept every writable scalar property (`hideMembers` —
`Default` or `HideBlankMembers` — `lineageTag`, `sourceLineageTag`) —
everything `tx get` shows for the hierarchy. Levels accept `ordinal`,
`lineageTag`, and `sourceLineageTag`; address a level with its full
`Table/Hierarchy/Level` path.

Partitions accept `description`, `mode` (`Import`, `DirectQuery`,
`Default`, `Push`, `Dual`, `DirectLake`), `dataView`, and `queryGroup` —
which must name an existing query group; an empty value clears it.
`retainDataTillForceCalculate` is calculated-source-only, just as
`expression` is M-source-only, and the set hint omits a source-bound
property a partition cannot take.

Relationships accept `name`, `isActive`, `crossFilteringBehavior`
(`OneDirection`, `BothDirections`, `Automatic`), `fromCardinality` and
`toCardinality` (`One`, `Many`), `securityFilteringBehavior`
(`OneDirection`, `BothDirections`, `None`), `relyOnReferentialIntegrity`,
and `joinOnDateBehavior` (`DateAndTime`, `DatePartOnly`). Address a
relationship by its endpoints:

```sh
tx set "Sales[OrderId]->Dates[Date]" --set isActive=false
```

The endpoint columns themselves stay read-only, and cardinality or
active-state edits surface in `diff` through the relationship's detail
line.

Roles accept `name`, `description`, and `modelPermission` (`None`,
`Read`, `ReadRefresh`, `Refresh`, `Administrator`). Role members accept
`name` or `memberName`, `memberId`, and — external members only —
`identityProvider` and `memberType` (`Auto`, `User`, `Group`); TOM
freezes a member's identity once attached, so changing any of these
fields replaces the member under the hood, and Windows members get a
clear error for the provider fields. Table permissions accept
`filterExpression` and `metadataPermission` (`Default`, `None`, `Read`):

```sh
tx set Readers/user@contoso.com -t member --set memberType=Group
tx set Readers/Customer --set metadataPermission=None
```

Shared expressions and functions accept `name`, `description`, and
`expression`; expressions also accept `kind` and `remoteParameterName`,
and functions accept `isHidden` — all of them carry lineage tags:

```sh
tx set "Expressions/Environment" --set remoteParameterName=RangeStart
```

The model root is addressed with `.`: `compatibilityLevel`,
`description`, `culture`, `collation`, `discourageImplicitMeasures`,
`discourageCompositeModels`, `defaultMode` (`Import`, `DirectQuery`,
`Default`, `Push`, `Dual`, `DirectLake`), `defaultDataView` (`Full`,
`Sample`, `Default`), `maxParallelismPerQuery`,
`maxParallelismPerRefresh`, `sourceQueryCulture`, and
`forceUniqueNames` are all settable, and `tx get .` reads the whole
surface back:

```sh
tx set . --set culture=en-US --save
tx get . --query defaultMode
```

Calculation-group tables accept `precedence` — a plain table rejects
it — and calculation items accept `description`, `expression`, and
`ordinal` via their `Table/Item` path.

Data sources accept `description`, `maxConnections`, and — provider
sources only — `impersonationMode` (`Default`, `ImpersonateAccount`,
`ImpersonateAnonymous`, `ImpersonateCurrentUser`,
`ImpersonateServiceAccount`, `ImpersonateUnattendedAccount`),
`isolation` (`ReadCommitted`, `Snapshot`), and `timeout` (seconds);
structured sources accept `contextExpression`. Connection strings,
accounts, and passwords are secrets, so they are never accepted via
argv — edit the source file to change credentials:

```sh
tx set DataSources/Import --set maxConnections=5
```

## `mv` — move or rename

```
tx mv <source> <destination> [model] [options]
```

Aliases: `move`, `rename`.

Renames rewrite referencing DAX automatically; `--strict-refs` and
`--no-fix-refs` behave as on `set`.

**Display folders.** Middle path segments are display folders, so `mv`
moves measures, columns, and hierarchies in and out of folders within
their table (nested folders as deeper segments). A destination ending in
`/` keeps the source name. Folder segments are only applied when either
path names them — a plain rename never touches the folder the object is
in; to move an object out of its folder, write the folder-qualified
source. A 3-segment path that matches a hierarchy level keeps its level
meaning — use `-t` when a level and a folder path could collide. Folder
changes never affect DAX, so no reference fixup runs for them.

Measures can also move to another table (optionally renaming and picking
a folder in the same step) — the classic "consolidate into a measure
table" operation. A move rewrites fully-qualified `'Table'[Measure]`
references to the new home table; unqualified `[Measure]` references stay
valid and are left alone. Columns, hierarchies, and partitions are bound
to their table's data and cannot move.

```sh
tx mv "Sales/Old Name" "Sales/New Name" --save
tx mv tables/Sales tables/SalesData
tx mv "Sales/Total Sales" "Metrics/Total Sales" --save
tx mv "Sales/Revenue" "Sales/Finance/Revenue" --save     # into a folder
tx mv "Sales/Finance/Revenue" "Sales/Revenue" --save     # out of the folder
tx mv "Sales/Finance/Revenue" "Sales/Margins/" --save    # between folders, keep name
tx rename "Sales/Date" "Sales/CalendarDate" -t Hierarchy --save
```

`mv --save` overwrites the source (and syncs the workspace mirror), so it
asks for confirmation; pass `--yes` to skip the prompt in scripts.
`--revert` (drops staged work) asks too. Plain `mv` stays in memory,
`--save-to` writes a copy, and `--stage` defers the prompt to
`tx stage commit`.

## `rm` — remove an object

Alias: `remove`.

```
tx rm <path> [model] [options]
```

| Option | Description |
|--------|-------------|
| `--dry-run` | Preview: show the change without saving, staging, or syncing. |
| `--force` | Remove even if the object has DAX dependents (reports the now-broken references). |
| `--if-exists` | Exit 0 when the object is already gone. |
| `-t, --type <type>` | Type to pick when the path matches several objects under a table. |

`--dry-run` previews instead of executing: it prints
`Would remove: <path>` and exits 0 without touching the model. When the
guard would block the removal, the preview lists the dependents
(`Would break N DAX reference(s) in: ...`) and hints `--force`, so a dry
run is how you discover that `--force` is needed.

Removal is blocked while DAX still references the object; structural
references (relationships, sort-by, hierarchy levels, perspectives, role
permissions) cascade-remove instead. Every object kind a mutation path can
address is removable: tables, measures, columns, hierarchies, levels,
partitions, calculation items, relationships, roles, role members,
perspectives, cultures, shared expressions, functions, data sources,
KPIs, table permissions, and calendars.
A data source still bound to a partition cannot be removed until the
partition is repointed or removed. A KPI shares its measure's path, so
address it explicitly: `tx rm "Sales/Total/KPI"` or
`tx rm "Sales/Total" -t kpi` (the measure survives; removing the measure
takes its KPI with it).

```sh
tx rm "Sales/Obsolete" --dry-run
tx rm tables/Staging --save
tx rm "Sales[CustomerID] -> Customers[CustomerID]" --save
```

## `replace` — find and replace

```
tx replace [pattern] [replacement] [model] [options]
```

| Option | Description |
|--------|-------------|
| `--in <scope>` | `names`, `expressions`, `descriptions`, `displayFolders`, `formatStrings`, `annotations`, `all` (default; excludes annotations). |
| `-t, --type <type>` | Only replace in objects of this kind (same vocabulary as `ls --type`). |
| `--regex` | Treat the pattern as a regular expression. |
| `--case-sensitive` | Case-sensitive matching. |
| `--dry-run` | Preview changes without applying. |

`--in expressions` walks every expression-bearing property: measure DAX,
detail-rows and format-string definitions, KPI target/status/trend, calculated
columns, partition M and calculated-table DAX, refresh-policy source and
polling M, calculation-group items and selection expressions, role
table-permission filters, shared expressions, and DAX functions. `--in names`
covers every renameable object, including role members; table-permission
names are excluded because the engine derives them from the table. `all` deliberately
excludes annotations — their values are often tool-generated JSON — so
annotations are only touched when requested explicitly (Tabular Editor's CLI
includes them in `all`). Whatever `tx find` reports for a scope, `tx replace`
rewrites in that scope; a test enforces the pairing.

```sh
tx replace "[OrderDate]" "[ShipDate]" --dry-run
tx replace "old_name" "new_name" --in names --save
tx replace "Sales" "Revenue" -t measure --dry-run
```

## `format` — format DAX and M

```
tx format [model] [options]
```

DAX is formatted offline by the engine bundled with `tx` — no network, no rate limits, same
result air-gapped. DAX output uses the bundled formatter's style: a 65-column prettier-style
layout with keywords and known function names upper-cased, so results differ from the
daxformatter.com style previous releases produced (and from Power BI's format button).
DAX that does not parse is left unchanged and reported with its line and column
(`DAX syntax error on line 1, column 19: Expected ',' or ')', but found 'Sales'.`), with the
same caret and `syntaxErrors` as M below.

Power Query (M) is formatted offline too, by Microsoft's
[powerquery-formatter](https://github.com/microsoft/powerquery-formatter) bundled inside `tx`
and run in-process — no network, no Node.js, nothing else to install. M is wrapped at 40
columns (120 with `--long`) with four-space indentation, so results can differ from the
network formatter previous releases used. M that does not lex or parse is left
unchanged and reported with its line and column (`M syntax error on line 3, column 1: ...`),
counted from the start of the expression. For an inline `-e` expression, text output also
shows the offending line with a caret under the error:

```text
Error: Formatting failed: M syntax error on line 1, column 12: A comma cannot proceed an 'in'
1 | let x = 1, in x
  |            ^^
```

With `--output-format json`, an inline or `--path` failure is a `TOMIX_FORMAT_FAILED` error
whose `syntaxErrors` array gives the stage, code, and span of each error (see
[error codes](../error-codes.md)).

With no target, formats every measure (DAX) or every partition (`--lang m`) in the model. Objects that fail to format are counted in `Failed: N` and the formatter's error
is reported per object on stderr (deduplicated with a `(+N more)` count when objects share
the same failure). With `--output-format json`, each failed result row carries an `error`
object: `message` always, plus `stage`, `code`, `line`, `column`, `endLine`, and `endColumn`
when the failure is a syntax error:

```json
{
  "table": "Category",
  "status": "failed",
  "partition": "Category-25da50ca",
  "error": {
    "message": "M syntax error on line 4, column 1: A comma cannot proceed an 'in'",
    "stage": "parse",
    "code": "expectedCsvContinuation",
    "line": 4,
    "column": 1,
    "endLine": 4,
    "endColumn": 2
  }
}
```

If any object fails, nothing is applied, saved, or staged: the run exits 1 with
`TOMIX_FORMAT_FAILED` (`No changes applied: N of M expressions failed to format.`), and the
text summary shows `Formatted: N (not applied)`. The result rows are still written, so the
counts show what would change once the failures are fixed.

Formatted DAX and M are syntax-highlighted in text output, for both inline `-e`
and `--path`; piping or redirecting strips the color, so the output stays safe to
copy back into a model.

| Option | Description |
|--------|-------------|
| `-e, --expression <expr>` | Format an inline expression (no model needed). |
| `--path <path>` | Format the expression on one object. |
| `--lang <dax\|m>` | Expression language. |
| `--long` | Prefer long lines when formatting M (120 columns instead of 40). |

```sh
tx format -e "CALCULATE(sum(sales[amt]))"
tx format --path "Sales/Total Sales" --save
tx format --save                     # whole model
```

## Refresh policies

Policies are table child objects, inspected and edited with `get`, `set`, and `rm`:

```sh
tx get 'Sales/RefreshPolicy' --model ./model.tmdl
tx set 'Sales/RefreshPolicy' -p 'IncrementalPeriods=7' --model ./model.tmdl --save
tx rm 'Sales/RefreshPolicy' --model ./model.tmdl --save
```

`-p` is an alias for `--set`; repeat either option to apply multiple assignments
as one edit.

| Property | Description |
|----------|-------------|
| `Mode` | `Import` (default) or `Hybrid`; hybrid requires a compatible model. |
| `RollingWindowPeriods`, `RollingWindowGranularity` | Archive window length and unit: day, month, quarter, year. |
| `IncrementalPeriods`, `IncrementalGranularity` | Incremental refresh window length and unit. |
| `IncrementalPeriodsOffset` | Offset of the refresh window. |
| `SourceExpression` | M query referencing `RangeStart` and `RangeEnd`. |
| `PollingExpression` | Optional change-detection M query; an empty value clears it. |

To create a policy, supply both window lengths and granularities and its source
expression together. Missing `RangeStart`/`RangeEnd` DateTime parameters are
created automatically. For example, read the source query from `source.m`:

```sh
cat source.m | tx set 'Sales/RefreshPolicy' --model ./model.tmdl \
  -p 'RollingWindowPeriods=10' -p 'RollingWindowGranularity=Year' \
  -p 'IncrementalPeriods=3' -p 'IncrementalGranularity=Day' \
  -p 'SourceExpression=-' --save
```

In PowerShell, use `Get-Content -Raw source.m` to feed the pipeline.
Policy inspection includes validation findings and generated partition names.
`--force` permits saving a policy with validation errors but cannot bypass TOM
compatibility requirements. Save, save-to, stage, revert, dry-run, and no-sync
follow the standard mutation lifecycle. Removing a policy leaves its generated
partitions in place and reports them; `--if-exists` permits a missing policy.

### Migration from `incremental-refresh`

The `incremental-refresh` command has been removed.

| Previous workflow | Replacement |
|-------------------|-------------|
| `incremental-refresh show Sales` | `get Sales/RefreshPolicy` |
| `incremental-refresh set Sales ...` | `set Sales/RefreshPolicy -p Property=Value ...` |
| `incremental-refresh rm Sales` | `rm Sales/RefreshPolicy` |
| Apply policy and load data | `refresh --table Sales --apply-refresh-policy true` |
| Apply without loading data (`apply --no-refresh`) | `refresh --table Sales --policy-only` |

Refresh operates on the policy **already saved on the deployed model**. Save or
deploy local policy edits first. See [refresh](connect.md#refresh-trigger-a-data-refresh)
for remote targeting, previews, and confirmation. Tomix does not require an
`--execute` flag; use `--policy-only` for empty-partition bootstrap.
