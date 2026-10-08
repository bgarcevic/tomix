# Commands

The command surface, grouped the way `tx --help` groups it. Every command
supports `--help` (or `tx help <command>`, e.g. `tx help bpa run`) with
options and examples — that output is always the authoritative reference for
the version you have installed. Run bare `tx` for just the command list.

| Group | Commands |
|-------|----------|
| [Discover](discover.md) | `summary`, `ls`, `get`, `find`, `deps`, `query` |
| [Modify](modify.md) | `add`, `set`, `mv`, `rm`, `replace`, `format`, `interactive`, `serve`, `ui`, `mcp` |
| [Connect](connect.md) | `connect`, `deploy`, `refresh`, `save`, `auth`, `session` |
| [Validate](validate.md) | `bpa`, `validate`, `test`, `vertipaq`, `diff`, `doctor` |
| [Manage](manage.md) | `config`, `profile`, `init`, `completion`, `stage`, `update` |

## Global options

These are accepted by every command and are not repeated on the individual
pages:

| Option | Description |
|--------|-------------|
| `-m, --model <path>` | Path to the semantic model: a TMDL folder, a `.bim` file, or a model folder. |
| `-s, --server <workspace>` | Workspace or server to connect to: a workspace name, a `powerbi://` or `asazure://` endpoint, an Analysis Services server, or a local address. See [server values](#server-values). |
| `-d, --database <name>` | Name of the semantic model to use on the workspace. |
| `--auth <method>` | How to authenticate: `auto` (default), `interactive`, `spn`, or `managed-identity`. |
| `--recent [<n>]` | Pick a model from the recent list: no value opens a picker, `n` picks that entry. Alias: `--recents`. |
| `--output-format <format>` | Format for data written to stdout: `text` (default), `json`, `csv`, `tmsl` (alias: `bim`), `tmdl`. Availability varies by command. |
| `--error-format <format>` | Format for messages written to stderr: `text` (default) or `json`. |
| `--non-interactive` | Never prompt for input; fail with an error saying what to provide. |
| `-y, --yes` | Skip confirmation prompts. `deploy` and partition-risky `refresh` variants preview first by default; `--yes` applies them without the preview. |
| `-q, --quiet` | Suppress non-essential output (spinners, progress, hints). Errors and data still print. |
| `--debug` | Show the full stack trace on stderr when an unexpected error occurs. |

### Server values

`-s/--server` (and the first argument of `tx connect`) is read as follows:

| Value | Connects to |
|-------|-------------|
| `Sales` | The Power BI workspace `Sales` (`powerbi://api.powerbi.com/v1.0/myorg/Sales`). |
| `powerbi://…`, `asazure://…` | That endpoint, signed in with `tx auth login`. |
| `localhost:<port>` | A running Power BI Desktop instance. |
| `ssas01.contoso.com`, `10.0.0.5`, `ssas01:2383`, `ssas01.contoso.com\TABULAR` | That Analysis Services server, as typed, with your Windows identity. |
| `Provider=MSOLAP;Data Source=SSAS01\TABULAR;…` | The connection string as written, for anything the forms above cannot express. |

A named instance on a single-word host (`SSAS01\TABULAR`) reads exactly like a
relative path, so give the host's full name or pass a connection string. A
dotted two-part name such as `Sales.Prod` connects to the server of that name;
`deploy` warns (`TOMIX_DEPLOY_AMBIGUOUS_SERVER`), and the full `powerbi://`
endpoint targets a workspace with that name instead.

`tx config set defaultFormat json` changes the implicit stdout format to JSON
for commands that support it. An explicit `--output-format` always wins;
completion scripts remain text regardless of this preference.

## Object paths

Commands address model objects by **path**: slash-separated
(`Sales/Total Sales`, `Sales/Partitions/Sales-2024`), with DAX forms also
accepted (`'Sales'[Total Sales]`). Bare names match literally; container
keywords pivot (`Tables`, `Measures`, `Sales/Partitions`); `*` is a wildcard
(`Sa*`, `*/Amount`). Quote names containing spaces; inside quotes, `''` is a
literal apostrophe. When a path matches multiple objects, disambiguate with
`-t/--type`.
