# Commands

The command surface, grouped the way `tx --help` groups it. Every command
supports `--help` (or `tx help <command>`, e.g. `tx help bpa run`) with
options and examples — that output is always the authoritative reference for
the version you have installed. Run bare `tx` for just the command list.

| Group | Commands |
|-------|----------|
| [Discover](discover.md) | `summary`, `ls`, `get`, `find`, `deps`, `query` |
| [Modify](modify.md) | `add`, `set`, `mv`, `rm`, `replace`, `format` |
| [Connect](connect.md) | `connect`, `deploy`, `refresh`, `save`, `auth`, `session` |
| [Validate](validate.md) | `bpa`, `validate`, `test`, `vertipaq`, `diff`, `doctor` |
| [Manage](manage.md) | `config`, `profile`, `init`, `completion`, `stage`, `update` |

## Global options

These are accepted by every command and are not repeated on the individual
pages:

| Option | Description |
|--------|-------------|
| `-m, --model <path>` | Path to the semantic model: a TMDL folder, a `.bim` file, or a model folder. |
| `-s, --server <workspace>` | Workspace to connect to: a name, a `powerbi://` or `asazure://` endpoint, or a local address. |
| `-d, --database <name>` | Name of the semantic model to use on the workspace. |
| `--auth <method>` | How to authenticate: `auto` (default), `interactive`, `spn`, or `managed-identity`. |
| `--recent [<n>]` | Pick a model from the recent list: no value opens a picker, `n` picks that entry. Alias: `--recents`. |
| `--output-format <format>` | Format for data written to stdout: `text` (default), `json`, `csv`, `tmsl` (alias: `bim`), `tmdl`. Availability varies by command. |
| `--error-format <format>` | Format for messages written to stderr: `text` (default) or `json`. |
| `--non-interactive` | Never prompt for input; fail with an error saying what to provide. |
| `-y, --yes` | Skip confirmation prompts for destructive operations. |
| `-q, --quiet` | Suppress non-essential output (spinners, progress, hints). Errors and data still print. |
| `--debug` | Show the full stack trace on stderr when an unexpected error occurs. |

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
