# PRD: MDL — the open semantic model CLI

## Product summary

**MDL** is a short, scriptable, open-source CLI for working with tabular semantic models.

The command is:

```bash
mdl
```

The product should focus on four use cases:

1. **Developers** working with semantic models in Git.
2. **BI teams** inspecting, editing, validating, and deploying models.
3. **CI/CD pipelines** running model checks and deployments.
4. **AI agents** reading and modifying models through predictable JSON output.

MDL should not try to clone Tabular Editor CLI command-for-command. It should become the open, composable, automation-first CLI for semantic models.

---

# Product principles

## 1. Safe by default

Any destructive or remote write operation should require explicit confirmation or `--force`.

```bash
mdl deploy ./model --workspace Prod --model Sales --dry-run
mdl deploy ./model --workspace Prod --model Sales --force
```

## 2. JSON everywhere

Every command should support:

```bash
--format json
```

Human output is nice. JSON output is required.

## 3. CI-friendly from the start

Every command should have predictable exit codes.

Suggested exit codes:

| Code | Meaning                      |
| ---: | ---------------------------- |
|  `0` | Success                      |
|  `1` | General failure              |
|  `2` | Invalid command or arguments |
|  `3` | Validation failed            |
|  `4` | Authentication failed        |
|  `5` | Connection failed            |
|  `6` | Deployment failed            |
|  `7` | Tests failed                 |
|  `8` | Unsafe operation blocked     |

## 4. Local-first, remote-second

Start by supporting local models well:

```bash
mdl ls ./Sales.SemanticModel
mdl ls ./model.bim
```

Then support remote XMLA/Fabric/Power BI models:

```bash
mdl ls --workspace Finance --model Sales
```

## 5. Do not overload commands

Prefer:

```bash
mdl get
mdl set
mdl add
mdl rm
mdl mv
```

over large, deeply nested command hierarchies.

---

# Recommended command style

Global shape:

```bash
mdl <command> [model-or-object] [options]
```

Examples:

```bash
mdl ls ./Sales.SemanticModel
mdl get ./Sales.SemanticModel "Sales/[Revenue]"
mdl find ./Sales.SemanticModel "margin"
mdl validate ./Sales.SemanticModel --format json
```

Remote examples:

```bash
mdl connect --workspace Finance --model Sales
mdl query -q "EVALUATE ROW(\"x\", 1)"
mdl refresh --type full
```

Object path syntax:

```text
Table
Table/Column
Table/[Measure]
Table/Hierarchy
Table/Partition
```

Examples:

```bash
mdl get "Sales/[Revenue]"
mdl get "Date/Calendar Year"
mdl get "Sales/Sales Amount"
```

---

# Prioritized build order

## P0 — CLI foundation

This is the base layer. Build this before any real semantic-model features.

### Goal

Create a professional CLI skeleton that can grow without redesign.

### Commands

| Command          | Purpose                    |
| ---------------- | -------------------------- |
| `mdl --help`     | Show help                  |
| `mdl --version`  | Show version               |
| `mdl doctor`     | Check local environment    |
| `mdl config`     | Manage local MDL config    |
| `mdl completion` | Generate shell completions |

---

## P1 — Local read-only model inspection

This is the first useful product.

### Goal

Allow users to inspect local semantic models without changing anything.

### Commands

| Command    | Purpose                               |
| ---------- | ------------------------------------- |
| `mdl info` | Show model summary                    |
| `mdl ls`   | List model objects                    |
| `mdl get`  | Get object details                    |
| `mdl find` | Search model metadata and expressions |
| `mdl diff` | Compare two local models              |

This should be the first public alpha.

---

## P2 — Remote connection and query

### Goal

Allow MDL to connect to Power BI/Fabric/XMLA models and run DAX queries.

### Commands

| Command          | Purpose                            |
| ---------------- | ---------------------------------- |
| `mdl auth`       | Authenticate                       |
| `mdl connect`    | Save or select active remote model |
| `mdl query`      | Run DAX queries                    |
| `mdl disconnect` | Clear active connection            |

This makes MDL useful for real models, not just local files.

---

## P3 — Validation and CI

### Goal

Make MDL useful in pull requests and build pipelines.

### Commands

| Command        | Purpose                                  |
| -------------- | ---------------------------------------- |
| `mdl validate` | Validate model structure and expressions |
| `mdl rules`    | Run custom quality rules                 |
| `mdl format`   | Format or check DAX expressions          |
| `mdl ci`       | Emit CI-specific annotations             |

This is where MDL starts becoming a DevOps tool.

---

## P4 — Safe local editing

### Goal

Allow users and agents to change local model files safely.

### Commands

| Command       | Purpose                |
| ------------- | ---------------------- |
| `mdl set`     | Set properties         |
| `mdl add`     | Add objects            |
| `mdl rm`      | Remove objects         |
| `mdl mv`      | Rename or move objects |
| `mdl replace` | Bulk replace text      |
| `mdl save`    | Save staged changes    |

Mutating commands should support:

```bash
--dry-run
--save
--format json
```

---

## P5 — Deployment and refresh

### Goal

Allow CI/CD pipelines to deploy and refresh semantic models.

### Commands

| Command       | Purpose                               |
| ------------- | ------------------------------------- |
| `mdl deploy`  | Deploy model metadata                 |
| `mdl refresh` | Trigger model refresh                 |
| `mdl process` | Process tables or partitions          |
| `mdl script`  | Generate XMLA/TMSL deployment scripts |

This is high-value but should come after validation and safe editing.

---

## P6 — Semantic model tests

### Goal

Allow teams to write regression tests against model results.

### Commands

| Command            | Purpose                     |
| ------------------ | --------------------------- |
| `mdl test init`    | Generate starter tests      |
| `mdl test run`     | Run semantic model tests    |
| `mdl test explain` | Explain failing test output |

This is a major differentiator.

---

## P7 — Performance and dependency analysis

### Goal

Help users understand model size, dependencies, and optimization opportunities.

### Commands

| Command        | Purpose                                 |
| -------------- | --------------------------------------- |
| `mdl deps`     | Show object dependencies                |
| `mdl lineage`  | Show dependency paths                   |
| `mdl vertipaq` | Show storage statistics                 |
| `mdl size`     | Show size summary                       |
| `mdl impact`   | Show what would be affected by a change |

---

## P8 — Agent and plugin ecosystem

### Goal

Make MDL extensible and excellent for AI agents.

### Commands

| Command       | Purpose                                         |
| ------------- | ----------------------------------------------- |
| `mdl mcp`     | Start an MCP server for AI agents               |
| `mdl plugin`  | Manage plugins                                  |
| `mdl schema`  | Print JSON schemas                              |
| `mdl explain` | Explain errors, objects, or validation failures |

This should come after the CLI itself is stable.

---

# Detailed command spec

## `mdl doctor`

Checks whether the local environment is ready.

```bash
mdl doctor
```

Should check:

* MDL version
* .NET/runtime availability, if relevant
* Config folder permissions
* Auth token cache availability
* Whether required providers are installed
* Whether shell completions are configured
* Whether local model paths are readable

Example output:

```text
MDL doctor

✓ MDL version: 0.1.0
✓ Config directory found
✓ Token cache available
✗ No active connection
```

JSON output:

```bash
mdl doctor --format json
```

---

## `mdl config`

Reads and writes local MDL configuration.

```bash
mdl config list
mdl config get defaultFormat
mdl config set defaultFormat json
mdl config set noColor true
```

Config should store preferences, not secrets.

Good config values:

```text
defaultFormat
noColor
telemetry
activeProfile
hideWarnings
```

---

## `mdl completion`

Generates shell completions.

```bash
mdl completion bash
mdl completion zsh
mdl completion fish
mdl completion powershell
```

This is not core functionality, but it makes the CLI feel serious.

---

# Model inspection commands

## `mdl info`

Shows a high-level summary of a model.

```bash
mdl info ./Sales.SemanticModel
```

Should show:

* Model name
* Compatibility level
* Tables
* Columns
* Measures
* Relationships
* Roles
* Perspectives
* Calculation groups
* Partitions
* Data sources
* Last modified, if available

Example:

```text
Sales

Tables:            18
Columns:           312
Measures:          74
Relationships:     41
Roles:             3
Calculation groups: 1
```

JSON:

```bash
mdl info ./Sales.SemanticModel --format json
```

---

## `mdl ls`

Lists objects in a model.

```bash
mdl ls ./Sales.SemanticModel
```

Default behavior should list tables.

Useful options:

```bash
mdl ls ./model --type table
mdl ls ./model --type measure
mdl ls ./model --type column
mdl ls ./model --type relationship
mdl ls ./model --hidden
mdl ls ./model --recursive
mdl ls ./model --format json
```

Examples:

```bash
mdl ls ./model --type measure
mdl ls ./model Sales
mdl ls ./model Sales --recursive
```

I would make `ls` read-only forever. It should never mutate anything.

---

## `mdl get`

Gets details for one object.

```bash
mdl get ./model "Sales/[Revenue]"
```

Should support:

```bash
mdl get ./model "Sales/[Revenue]"
mdl get ./model "Sales/[Revenue]" --property expression
mdl get ./model "Sales/[Revenue]" --property formatString
mdl get ./model "Date/Calendar Year"
mdl get ./model "Sales"
```

Useful aliases:

```bash
mdl get ./model "Sales/[Revenue]" -p expression
```

Expected behavior:

* If object is a measure, show expression, format string, description, hidden state.
* If object is a column, show data type, source column, format string, summarization, hidden state.
* If object is a table, show columns, measures, partitions, row count if available.
* If object is not found, suggest closest matches.

Example error:

```text
Object not found: Sales/[Revnue]

Did you mean?
  Sales/[Revenue]
  Sales/[Net Revenue]
```

---

## `mdl find`

Searches a model.

```bash
mdl find ./model "margin"
```

Should search:

* Object names
* DAX expressions
* Descriptions
* Format strings
* Display folders
* Annotations
* Source expressions
* Role filters

Useful options:

```bash
mdl find ./model "margin" --in expressions
mdl find ./model "margin" --type measure
mdl find ./model "customer" --case-sensitive
mdl find ./model "SUMX" --regex
mdl find ./model "margin" --format json
```

Example output:

```text
Path                    Type      Property      Match
Sales/[Gross Margin]    Measure   Name          Gross Margin
Sales/[Margin %]        Measure   Expression    DIVIDE([Gross Profit], [Revenue])
```

This command is very important for AI agents.

---

## `mdl diff`

Compares two models.

```bash
mdl diff ./model-a ./model-b
```

Should show:

* Added tables
* Removed tables
* Renamed objects, if detectable
* Changed expressions
* Changed relationships
* Changed roles
* Changed partitions
* Changed annotations

Useful options:

```bash
mdl diff ./model-a ./model-b --format json
mdl diff ./model-a ./model-b --ignore-formatting
mdl diff ./model-a ./model-b --type measure
mdl diff ./model-a ./model-b --output patch.json
```

Example:

```text
Changed measures

~ Sales/[Revenue]
  expression changed

+ Sales/[Gross Margin]
- Sales/[Old Margin]
```

Later, this could power deployment previews.

---

# Authentication and connection commands

## `mdl auth`

Manages authentication.

```bash
mdl auth login
mdl auth logout
mdl auth status
mdl auth env
```

Subcommands:

| Command           | Purpose                                          |
| ----------------- | ------------------------------------------------ |
| `mdl auth login`  | Interactive browser/device login                 |
| `mdl auth logout` | Clear local token                                |
| `mdl auth status` | Show current auth state                          |
| `mdl auth env`    | Validate service-principal environment variables |

Example:

```bash
mdl auth login
```

For CI:

```bash
mdl auth env
```

Expected environment variables:

```text
AZURE_TENANT_ID
AZURE_CLIENT_ID
AZURE_CLIENT_SECRET
```

Do not store secrets in MDL config.

---

## `mdl connect`

Sets the active remote model.

```bash
mdl connect --workspace Finance --model Sales
```

Should support:

```bash
mdl connect --workspace Finance --model Sales
mdl connect --server "powerbi://api.powerbi.com/v1.0/myorg/Finance" --database Sales
mdl connect --profile prod
mdl connect --list
```

Expected behavior:

* Validates the connection.
* Saves it for the current shell/profile.
* Lets later commands omit workspace/model flags.

Example:

```bash
mdl connect --workspace Finance --model Sales
mdl ls
mdl query -q "EVALUATE ROW(\"x\", 1)"
```

---

## `mdl disconnect`

Clears active connection.

```bash
mdl disconnect
```

This is simple but useful.

---

# Query commands

## `mdl query`

Runs a DAX query.

```bash
mdl query -q "EVALUATE ROW(\"Revenue\", [Revenue])"
```

Should support:

```bash
mdl query -q "EVALUATE TOPN(10, 'Customer')"
mdl query --file query.dax
mdl query --format json
mdl query --format csv
mdl query --output result.csv
```

Useful options:

```text
-q, --query
-f, --file
--format table|json|csv
--output
--timeout
--workspace
--model
--server
--database
```

Expected behavior:

* Validate connection.
* Execute query.
* Return result set.
* Return useful error on DAX failure.
* Support CSV and JSON for automation.

Example:

```bash
mdl query \
  --workspace Finance \
  --model Sales \
  -q "EVALUATE TOPN(10, 'Customer')"
```

This should be one of the flagship commands.

---

# Validation commands

## `mdl validate`

Validates a model.

```bash
mdl validate ./Sales.SemanticModel
```

Should check:

* Invalid DAX expressions
* Broken references
* Invalid relationships
* Duplicate names
* Missing source columns
* Invalid role filters
* Unsupported compatibility issues
* Dangerous modeling patterns, where detectable

Useful options:

```bash
mdl validate ./model --format json
mdl validate ./model --fail-on warning
mdl validate ./model --fail-on error
mdl validate ./model --ci github
mdl validate ./model --ci azure
```

Expected severities:

```text
info
warning
error
fatal
```

Example output:

```text
ERROR DAX001 Sales/[Revenue]
Unknown column: Sales[Amountt]

WARNING MODEL004 Customers[Customer Name]
Column is visible but has high cardinality.
```

Exit behavior:

* `0` if validation passes.
* `3` if validation fails at or above `--fail-on`.

---

## `mdl rules`

Runs user-defined quality rules.

```bash
mdl rules run ./model
```

Subcommands:

```bash
mdl rules init
mdl rules run ./model
mdl rules list
mdl rules explain RULE_ID
mdl rules fix ./model
```

Rules should be open and file-based.

Example rule file:

```yaml
id: MEASURE_DESCRIPTION_REQUIRED
severity: warning
target: measure
condition: description_is_empty
message: "Measure should have a description."
```

Useful options:

```bash
mdl rules run ./model --rules ./rules
mdl rules run ./model --fail-on error
mdl rules run ./model --ci github
mdl rules fix ./model --save
```

I would not call this `bpa`. Use `rules` to keep MDL independent and broader.

---

## `mdl format`

Formats DAX expressions.

```bash
mdl format ./model
```

Should support two modes:

```bash
mdl format -e "SUMX(Sales,Sales[Amount])"
mdl format ./model --check
mdl format ./model --save
```

Behavior:

* With `--check`, report expressions that are not formatted.
* With `--save`, rewrite formatted expressions.
* Without `--save`, only print changes.

Useful options:

```text
-e, --expression
--check
--save
--diff
--format json
```

This should come after basic validation because DAX formatting can become complicated.

---

# Editing commands

## `mdl set`

Sets a property on an object.

```bash
mdl set ./model "Sales/[Revenue]" --property description --value "Total revenue"
```

Convenience options:

```bash
mdl set ./model "Sales/[Revenue]" --expression "[Quantity] * [Net Price]"
mdl set ./model "Sales/[Revenue]" --format-string "$#,0"
mdl set ./model "Sales/[Revenue]" --hidden false
```

Behavior:

* Shows a preview by default.
* Requires `--save` to write changes.
* Supports `--format json`.

Example:

```bash
mdl set ./model "Sales/[Revenue]" \
  --description "Total recognized revenue." \
  --save
```

This will be one of the most important agent-facing commands.

---

## `mdl add`

Adds a new object.

```bash
mdl add measure ./model Sales "Revenue" --expression "SUM(Sales[Amount])"
```

Object types:

```bash
mdl add table
mdl add column
mdl add measure
mdl add relationship
mdl add role
mdl add perspective
mdl add partition
```

Examples:

```bash
mdl add measure ./model Sales "Gross Margin" \
  --expression "DIVIDE([Gross Profit], [Revenue])" \
  --format-string "0.00%" \
  --save
```

```bash
mdl add relationship ./model \
  --from Sales.CustomerKey \
  --to Customer.CustomerKey \
  --save
```

Behavior:

* Validate before adding.
* Refuse duplicate names unless `--replace`.
* Preview by default.
* Require `--save`.

---

## `mdl rm`

Removes an object.

```bash
mdl rm ./model "Sales/[Old Revenue]"
```

Should support:

```bash
mdl rm ./model "Sales/[Old Revenue]" --dry-run
mdl rm ./model "Sales/[Old Revenue]" --save
mdl rm ./model "Sales/[Old Revenue]" --force
```

Expected behavior:

* Show dependencies before deletion.
* Block deletion if other objects depend on it, unless `--force`.
* Require `--save`.

Example:

```text
Cannot remove Sales/[Old Revenue].
It is referenced by:

  Sales/[Revenue YoY]
  Sales/[Revenue %]

Use --force to remove anyway.
```

---

## `mdl mv`

Renames or moves an object.

```bash
mdl mv ./model "Sales/[GM]" "Sales/[Gross Margin]"
```

Should support:

```bash
mdl mv ./model "Sales/[GM]" "Sales/[Gross Margin]" --save
mdl mv ./model "Old Table" "New Table" --save
mdl mv ./model "Sales/Old Column" "Sales/New Column" --save
```

Expected behavior:

* Rename object.
* Update references where safe.
* Show impacted expressions.
* Refuse unsafe updates unless `--force`.

This command is deceptively hard. Build it carefully.

---

## `mdl replace`

Bulk find-and-replace.

```bash
mdl replace ./model "Gross Sales" "Revenue"
```

Useful options:

```bash
mdl replace ./model "Gross Sales" "Revenue" --in expressions
mdl replace ./model "Gross Sales" "Revenue" --in descriptions
mdl replace ./model "Gross Sales" "Revenue" --dry-run
mdl replace ./model "Gross Sales" "Revenue" --save
```

Behavior:

* Always preview changes.
* Show before/after snippets.
* Require `--save`.

This is useful for bulk cleanup and AI-assisted refactors.

---

## `mdl save`

Saves staged changes.

```bash
mdl save ./model
```

This only matters if MDL supports staged sessions later.

For the first version, I would make most commands use `--save` directly and defer `mdl save`.

---

# Deployment and refresh commands

## `mdl deploy`

Deploys a model.

```bash
mdl deploy ./Sales.SemanticModel --workspace Production --model Sales
```

Required behavior:

* Validate model before deploy by default.
* Show deployment summary.
* Support dry-run.
* Require confirmation or `--force`.
* Emit CI annotations.
* Optionally generate XMLA/TMSL script.

Useful options:

```bash
mdl deploy ./model --workspace Prod --model Sales --dry-run
mdl deploy ./model --workspace Prod --model Sales --force
mdl deploy ./model --workspace Prod --model Sales --script deploy.tmsl
mdl deploy ./model --workspace Prod --model Sales --skip-validation
mdl deploy ./model --workspace Prod --model Sales --ci github
```

Expected deploy summary:

```text
Deploy Sales to Production

Changed:
  4 measures
  1 table
  2 relationships

Validation:
  0 errors
  3 warnings

Proceed? [y/N]
```

This command should not be rushed. It can destroy production models.

---

## `mdl refresh`

Triggers a refresh.

```bash
mdl refresh --workspace Production --model Sales
```

Should support:

```bash
mdl refresh --type full
mdl refresh --type calculate
mdl refresh --table Sales
mdl refresh --partition Sales.2025
mdl refresh --wait
mdl refresh --status
mdl refresh --cancel
```

Examples:

```bash
mdl refresh --workspace Prod --model Sales --type full --wait
mdl refresh --table Sales --type full
mdl refresh --status
```

Expected behavior:

* Start refresh.
* Print refresh ID.
* Optionally wait until finished.
* Show table-level status where available.
* Return non-zero exit code if refresh fails.

---

## `mdl process`

Processes specific tables, partitions, or calculation operations.

```bash
mdl process --table Sales --type full
```

This overlaps with `refresh`, but I’d keep both:

* `refresh` is user-friendly and Power BI/Fabric-oriented.
* `process` is lower-level XMLA/Analysis Services terminology.

Possible process types:

```text
full
clear
dataOnly
calculate
automatic
defragment
```

This should be added after `refresh`.

---

## `mdl script`

Generates scripts without applying them.

```bash
mdl script deploy ./model --workspace Prod --model Sales --output deploy.tmsl
mdl script refresh --type full --output refresh.tmsl
```

Subcommands:

```bash
mdl script deploy
mdl script refresh
mdl script process
```

This is valuable for DBAs and cautious enterprise teams.

---

# Testing commands

## `mdl test init`

Generates starter tests.

```bash
mdl test init ./model
```

Should generate something like:

```yaml
tests:
  - name: Revenue is not blank
    query: |
      EVALUATE ROW("Revenue", [Revenue])
    assert:
      Revenue:
        notNull: true
```

Useful options:

```bash
mdl test init ./model --from-measures
mdl test init ./model --output ./tests
```

---

## `mdl test run`

Runs model tests.

```bash
mdl test run ./tests --workspace Dev --model Sales
```

Should support assertions like:

```yaml
tests:
  - name: Revenue is positive
    query: |
      EVALUATE ROW("Revenue", [Revenue])
    assert:
      Revenue:
        greaterThan: 0
```

Useful options:

```bash
mdl test run ./tests --format json
mdl test run ./tests --ci github
mdl test run ./tests --fail-fast
mdl test run ./tests --update-snapshots
```

Expected output:

```text
PASS Revenue is positive
PASS Customer count is above minimum
FAIL Gross Margin matches baseline

Expected: 0.42
Actual:   0.39
```

Exit code should be `7` when tests fail.

---

## `mdl test explain`

Explains a failing test.

```bash
mdl test explain ./tests/revenue.yml
```

Should show:

* Query
* Expected result
* Actual result
* Difference
* Possible cause
* Related measures

This is a good AI-agent hook later.

---

# Analysis commands

## `mdl deps`

Shows dependencies.

```bash
mdl deps ./model "Sales/[Gross Margin]"
```

Should answer:

* What does this object depend on?
* What depends on this object?

Useful options:

```bash
mdl deps ./model "Sales/[Gross Margin]" --direction in
mdl deps ./model "Sales/[Gross Margin]" --direction out
mdl deps ./model "Sales/[Gross Margin]" --format json
```

Example:

```text
Sales/[Gross Margin]
depends on:
  Sales/[Gross Profit]
  Sales/[Revenue]

used by:
  Sales/[Gross Margin YoY]
  Sales/[Gross Margin % Target]
```

---

## `mdl lineage`

Shows dependency paths.

```bash
mdl lineage ./model "Sales/[Gross Margin]"
```

This is deeper than `deps`.

`deps` shows immediate dependencies.

`lineage` shows chains:

```text
Sales/[Gross Margin]
└── Sales/[Gross Profit]
    ├── Sales/[Revenue]
    └── Sales/[Cost]
```

Useful for understanding complicated model logic.

---

## `mdl impact`

Shows what would be affected by a change.

```bash
mdl impact ./model "Sales/[Revenue]"
```

Should answer:

* What measures depend on it?
* What tests might be impacted?
* What reports might be impacted, if report metadata is later supported?
* What roles or perspectives include it?

This is especially useful before `rm`, `mv`, or `set`.

---

## `mdl size`

Shows a model size summary.

```bash
mdl size --workspace Prod --model Sales
```

Should show:

* Total size
* Table sizes
* Column sizes, if available
* Largest tables
* Largest columns
* Row counts, if available

This should be simpler than `vertipaq`.

---

## `mdl vertipaq`

Shows detailed VertiPaq storage statistics.

```bash
mdl vertipaq --workspace Prod --model Sales
```

Useful options:

```bash
mdl vertipaq --table Sales
mdl vertipaq --columns
mdl vertipaq --export model.vpax
mdl vertipaq --format json
```

Expected output:

```text
Table       Rows        Size
Sales       25,100,000  812 MB
Customer    1,200,000   104 MB
Date        10,000      2 MB
```

This is valuable, but not necessary for the first release.

---

# Agent and extensibility commands

## `mdl schema`

Prints JSON schemas for command outputs.

```bash
mdl schema list
mdl schema get ls
mdl schema get validate
```

This is extremely useful for AI agents and typed automation.

Example:

```bash
mdl schema get validate --output validate.schema.json
```

---

## `mdl explain`

Explains MDL errors or model issues.

```bash
mdl explain DAX001
mdl explain ./model "Sales/[Revenue]"
mdl explain --last-error
```

Should be able to explain:

* Validation rule IDs
* DAX errors
* Deployment errors
* Test failures
* Object dependencies

This can start simple and later become AI-powered.

---

## `mdl mcp`

Starts an MCP server for AI coding agents.

```bash
mdl mcp
```

The MCP server should expose safe tools like:

```text
list_model_objects
get_object
find_model
validate_model
run_dax_query
preview_change
apply_change
```

This should not be first, but it could become one of MDL’s strongest differentiators.

---

## `mdl plugin`

Manages plugins.

```bash
mdl plugin list
mdl plugin install mdl-rules-finance
mdl plugin remove mdl-rules-finance
```

Plugins could provide:

* Rules
* Commands
* Output formatters
* Test assertions
* Deployment providers

This is later-stage. Do not build too early.

---

# Final recommended command list

I would aim for this final command set:

```text
mdl
├── doctor
├── config
├── completion
├── info
├── ls
├── get
├── find
├── diff
├── auth
├── connect
├── disconnect
├── query
├── validate
├── rules
├── format
├── set
├── add
├── rm
├── mv
├── replace
├── deploy
├── refresh
├── process
├── script
├── test
├── deps
├── lineage
├── impact
├── size
├── vertipaq
├── schema
├── explain
├── mcp
└── plugin
```

But I would not build them all at once.

---

# Recommended implementation order

## First release candidate

Build only this:

```text
mdl doctor
mdl info
mdl ls
mdl get
mdl find
mdl query
mdl validate
```

This gives you a useful product.

## Second release candidate

Add:

```text
mdl auth
mdl connect
mdl diff
mdl rules
mdl format --check
```

This gives you a CI-ready product.

## Third release candidate

Add:

```text
mdl set
mdl add
mdl rm
mdl mv
mdl replace
```

This gives you an editing product.

## Fourth release candidate

Add:

```text
mdl deploy
mdl refresh
mdl script
```

This gives you a DevOps product.

## Fifth release candidate

Add:

```text
mdl test
mdl deps
mdl lineage
mdl impact
```

This gives you a serious semantic-model engineering product.

## Later

Add:

```text
mdl size
mdl vertipaq
mdl mcp
mdl plugin
mdl explain
mdl schema
```

This gives you an ecosystem.

---

# My opinionated MVP

The absolute best first public version would be:

```bash
mdl info ./model
mdl ls ./model
mdl get ./model "Sales/[Revenue]"
mdl find ./model "margin"
mdl query --workspace Dev --model Sales -q "EVALUATE ROW(\"x\", 1)"
mdl validate ./model --format json
```

That is enough to prove the concept, attract contributors, and give MDL a clear identity:

> **A small, open, scriptable semantic-model CLI.**
