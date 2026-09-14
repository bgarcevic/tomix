# Discover

Read-only commands for exploring a model. All of them work against the
[active connection](../guides/connections.md) or an explicit model argument,
and all print JSON with `--output-format json`. `ls`, `get`, and `query` also
print CSV; `get` additionally emits `tmdl`, `bim`, and `tmsl`.

## `ls` — list model objects

Alias: `list`.

```
tx ls [path-filter] [model] [options]
```

| Option | Description |
|--------|-------------|
| `-t, --type <type>` | Filter by type: `table`, `measure`, `column`, `calculatedcolumn`, `hierarchy`, `level`, `partition`, `calculationitem`, `member`, `relationship`, `role`, `perspective`, `culture`, `datasource`, `kpi`, `tablepermission`, `calendar`, `expression`, `function`. `column` matches data and calculated columns; `calculatedcolumn` narrows to calculated ones. |
| `--paths-only` | One object path per line, ready for piping. |
| `--no-multiline` | Show multi-line cell content (e.g. measure expressions) on one line. Text output only. |

```sh
tx ls                                # everything
tx list --type table --paths-only    # 'list' is an alias of 'ls'
tx ls Sales/Measures                 # children of a container
tx ls Expressions                    # shared M expressions (parameters)
tx ls Functions                      # DAX user-defined functions
tx ls "Sa*"                          # wildcard filter
tx ls --type calculatedcolumn        # only calculated columns
```

## `get` — properties of one object

```
tx get <path> [model] [options]
```

| Option | Description |
|--------|-------------|
| `--query <property>` | Read just one property (e.g. `--query expression`). |
| `-t, --type <type>` | Type to pick when the path matches several objects under a table. |

Each object kind has its own property set: measures include `expression`,
`formatString`, `detailRowsExpression`, and the KPI expressions; relationships
include their endpoint columns, cardinality, `crossFilteringBehavior`, and
`isActive`; roles include `modelPermission` and `rlsExpression`; shared
expressions include `expression`, `kind`, and `remoteParameterName`; DAX
functions include `expression` and `isHidden`. The model root has its own
path — `tx get .` reports the compatibility level, `culture`,
`defaultMode`, and the other model-level scalars. Object
annotations are appended as `annotation:<name>` entries in text and JSON
output (CSV keeps the fixed per-kind columns).

```sh
tx get "Sales/Total Sales"
tx get "Sales/Total Sales" --query expression
tx get "Sales/Total Sales" --query annotation:PBI_FormatHint
tx get "Relationships/rel-customers"
tx get "Expressions/Environment" --query expression   # a shared M parameter's value
tx get . --query culture                         # a model-level scalar
tx get Sales --output-format tmdl    # the object as TMDL
```

`-q` is the global `--quiet` flag, not a property shortcut — always pass the
property through `--query`.

## `find` — search across the model

```
tx find <pattern> [model] [options]
```

| Option | Description |
|--------|-------------|
| `--in <scope>` | Where to look: `names`, `expressions`, `descriptions`, `displayFolders`, `formatStrings`, `annotations`, `all` (default; annotations only when requested explicitly). |
| `-t, --type <type>` | Only search objects of this kind (same vocabulary as `ls --type`). |
| `--regex` | Interpret the pattern as a regular expression. |
| `--case-sensitive` | Match text exactly, including letter case. |
| `--paths-only` | One matching object path per line, ready for piping. |
| `--no-multiline` | Show multi-line match context on one line. Text output only. |

Searches every scope site `tx replace` can rewrite — including partition
expressions, KPI expressions, detail-rows and format-string definitions,
refresh-policy M, calculation-group selection expressions, RLS filter
expressions, shared M expressions, and DAX user-defined functions — so a
find preview is also a replace preview. Relationship
names are skipped (synthesized from endpoints, not authored text), but
relationship annotations are searched under `--in annotations`.

```sh
tx find "SUM" --in expressions
tx find "TODO|FIXME" --regex --in descriptions
tx find "Qty" -t measure --paths-only
```

## `deps` — dependency analysis

```
tx deps [path] [model] [options]
```

| Option | Description |
|--------|-------------|
| `--upstream` | Trace only what this object uses. |
| `--downstream` | Trace only what uses this object. |
| `--deep` | Walk the dependency chain recursively. |
| `--max-depth <n>` | How deep `--deep` walks (default: 10). |
| `--unused` | List measures and columns that nothing depends on. |
| `--hidden` | With `--unused`: restrict the list to unused objects that are hidden. |
| `-t, --type <type>` | Type to pick when the path matches several objects under a table. |

```sh
tx deps "Sales/Total Sales" --upstream
tx deps "Sales/Amount" --downstream --deep
tx deps --unused --hidden
```

## `query` — run DAX or DMV

```
tx query [query] [options]
```

Executes against a live model (the active remote connection, or
`-s`/`-d`). See [Output & scripting](../guides/scripting.md#querying-live-models)
for the performance-analysis workflow.

The query text can be passed positionally, via `--query`, `--file` (or
piped on stdin) — exactly one of the three. `-q` is the global `--quiet`
flag and is never query text; `query -q "EVALUATE …"` fails with
`TOMIX_QUIET_COLLISION` naming the mix-up.

| Option | Description |
|--------|-------------|
| `--query <text>` | Inline query (`-` = stdin). |
| `--file <file>` | Read the query from a file (`-` = stdin). |
| `--param <name=value>` | Query parameter, referenced as `@name` in DAX. Repeatable. |
| `--limit <n>` | Maximum rows to return. |
| `-o, --output-file <file>` | Write results to a file as json or csv. |
| `--trace [path]` | Server timings (formula vs storage engine); optional path dumps raw trace events. Needs admin rights. |
| `--plan` | Show logical and physical DAX query plans. Needs admin rights. |
| `--cold` | Clear the model cache before each run. Needs admin rights. |
| `--runs <n>` | Execute N times and report Avg/Min/Max/StdDev. |
| `--no-validate` | Skip the EVALUATE/DEFINE/SELECT keyword pre-check. |

```sh
tx query 'EVALUATE ROW("Sales", [Total Sales])' --trace --plan
tx query --file heavy.dax --cold --runs 5
```
