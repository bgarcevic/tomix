# Discover

Read-only commands for exploring a model. All of them work against the
[active connection](../guides/connections.md) or an explicit model argument,
and all print JSON/CSV with `--output-format`.

## `ls` — list model objects

```
tx ls [path-filter] [model] [options]
```

| Option | Description |
|--------|-------------|
| `--type <type>` | Filter by type: `table`, `measure`, `column`, `calculatedcolumn`, `hierarchy`, `partition`, `relationship`, `role`, `perspective`, `culture`, `kpi`, `tablepermission`, `calendar`. |
| `--paths-only` | One object path per line, suitable for piping. |
| `--no-multiline` | Collapse multi-line cell content (e.g. measure expressions) to a single line. Text output only. |

```sh
tx ls                                # everything
tx ls --type table --paths-only
tx ls Sales/Measures                 # children of a container
tx ls "Sa*"                          # wildcard filter
```

## `get` — properties of one object

```
tx get <path> [model] [options]
```

| Option | Description |
|--------|-------------|
| `-q, --query <property>` | Query a specific property (e.g. `-q expression`, `-q formatString`). |
| `-t, --type <type>` | Disambiguate when the path matches multiple table-children. |

Each object kind has its own property set: measures include `expression`,
`formatString`, `detailRowsExpression`, and the KPI expressions; relationships
include their endpoint columns, cardinality, `crossFilteringBehavior`, and
`isActive`; roles include `modelPermission` and `rlsExpression`. Object
annotations are appended as `annotation:<name>` entries in text and JSON
output (CSV keeps the fixed per-kind columns).

```sh
tx get "Sales/Total Sales"
tx get "Sales/Total Sales" -q expression
tx get "Sales/Total Sales" -q annotation:PBI_FormatHint
tx get "Relationships/rel-customers"
tx get Sales --output-format tmdl    # the object as TMDL
```

## `find` — search across the model

```
tx find <pattern> [model] [options]
```

| Option | Description |
|--------|-------------|
| `--in <scope>` | `names`, `expressions`, `descriptions`, `displayFolders`, `formatStrings`, `annotations`, `all` (default; annotations only when requested explicitly). |
| `--regex` | Treat the pattern as a regular expression. |
| `--case-sensitive` | Case-sensitive matching. |
| `--paths-only` | One matching object path per line, suitable for piping. |
| `--no-multiline` | Collapse multi-line match context to a single line. Text output only. |

Searches every scope site `tx replace` can rewrite — including partition
expressions, KPI expressions, detail-rows and format-string definitions,
refresh-policy M, calculation-group selection expressions, and RLS filter
expressions — so a find preview is also a replace preview. Relationship
names are skipped (synthesized from endpoints, not authored text), but
relationship annotations are searched under `--in annotations`.

```sh
tx find "SUM" --in expressions
tx find "TODO|FIXME" --regex --in descriptions
```

## `deps` — dependency analysis

```
tx deps [path] [model] [options]
```

| Option | Description |
|--------|-------------|
| `--upstream` | Only upstream dependencies (what this object uses). |
| `--downstream` | Only downstream dependents (what uses this object). |
| `--deep` | Recursive dependency tree. |
| `--max-depth <n>` | Maximum depth for `--deep` traversal (default: 10). |
| `--unused` | Find unreferenced measures and columns. |
| `--hidden` | With `--unused`: only list unused objects that are hidden. |
| `-t, --type <type>` | Disambiguate when the path matches multiple table-children. |

```sh
tx deps "Sales/Total Sales" --upstream
tx deps "Sales/Amount" --downstream --deep
tx deps --unused --hidden
```

## `query` — run DAX or DMV

```
tx query [options]
```

Executes against a live model (the active remote connection, or
`-s`/`-d`). See [Output & scripting](../guides/scripting.md#querying-live-models)
for the performance-analysis workflow.

| Option | Description |
|--------|-------------|
| `-q, --query <text>` | Inline query (`-` = stdin). |
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
tx query -q 'EVALUATE ROW("Sales", [Total Sales])' --trace --plan
tx query --file heavy.dax --cold --runs 5
```
