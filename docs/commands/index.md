# Commands

The command surface, grouped the way `tx --help` groups it. Every command
supports `--help` with options and examples — that output is always the
authoritative reference for the version you have installed.

| Group | Commands |
|-------|----------|
| [Discover](discover.md) | `ls`, `get`, `find`, `deps`, `query` |
| [Modify](modify.md) | `add`, `set`, `mv`, `rm`, `replace`, `format`, `script` |
| [Connect](connect.md) | `connect`, `deploy`, `refresh`, `load`, `save`, `auth`, `session` |
| [Validate](validate.md) | `bpa`, `validate`, `test`, `vertipaq`, `diff`, `doctor` |
| [Manage](manage.md) | `config`, `profile`, `init`, `completion`, `stage`, `update` |

## Global options

These are accepted by every command and are not repeated on the individual
pages:

| Option | Description |
|--------|-------------|
| `-m, --model <model>` | Path to the semantic model: a TMDL folder, a `.bim` file, or a model folder. |
| `-s, --server <server>` | Workspace to connect to: a name, a `powerbi://` or `asazure://` endpoint, or a local address. |
| `-d, --database <database>` | Name of the semantic model to use on the workspace. |
| `--auth <auth>` | How to authenticate: `auto` (default), `interactive`, `spn`, or `managed-identity`. |
| `--recent [N]` | Pick a model from the recent list: no value opens a picker, `N` picks that entry. |
| `--output-format <fmt>` | Format for data written to stdout: `text` (default), `json`, `csv`, `tmsl` (alias: `bim`), `tmdl`. Availability varies by command. |
| `--error-format <fmt>` | Format for messages written to stderr: `text` (default) or `json`. |
| `--non-interactive` | Never prompt for input; fail with an error saying what to provide. |
| `-y, --yes` | Skip confirmation prompts for destructive operations. |
| `--quiet` | Suppress non-essential output (spinners, progress, hints). Errors and data still print. |
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
