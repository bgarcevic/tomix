# Commands

The command surface, grouped the way `tx --help` groups it. Every command
supports `--help` with options and examples — that output is always the
authoritative reference for the version you have installed.

| Group | Commands |
|-------|----------|
| [Discover](discover.md) | `ls`, `get`, `find`, `deps`, `query` |
| [Modify](modify.md) | `add`, `set`, `mv`, `rm`, `replace`, `format`, `script`, `incremental-refresh` |
| [Connect](connect.md) | `connect`, `deploy`, `refresh`, `load`, `save`, `auth`, `session` |
| [Validate](validate.md) | `bpa`, `validate`, `test`, `vertipaq`, `diff`, `doctor` |
| [Manage](manage.md) | `config`, `profile`, `init`, `completion`, `stage`, `update` |

## Global options

These are accepted by every command and are not repeated on the individual
pages:

| Option | Description |
|--------|-------------|
| `-m, --model <model>` | Path to semantic model (TMDL folder, `.bim` file, or TE folder). |
| `-s, --server <server>` | Workspace name or endpoint (e.g. `MyWorkspace`, `powerbi://...`, `asazure://...`, `localhost`). |
| `-d, --database <database>` | Semantic model name on the workspace. |
| `--auth <auth>` | Auth method: `auto` (default), `interactive`, `spn`, `managed-identity`. |
| `--recent [N]` | Use a recently used model. No value = interactive picker, `N` = Nth most recent. |
| `--output-format <fmt>` | Stdout format: `text` (default), `json`, `csv`, `tmsl` (alias: `bim`), `tmdl`. Not every format is supported by every command. |
| `--error-format <fmt>` | Stderr format for errors/warnings/hints: `text` (default) or `json`. |
| `--non-interactive` | Disable all interactive prompts; fail with an actionable error if required input is missing. |
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
