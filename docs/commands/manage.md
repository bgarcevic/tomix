# Manage

Housekeeping: configuration, profiles, model scaffolding, shell
integration, and self-update.

## `config` — CLI configuration

```
tx config <show|set|init|paths>
```

| Subcommand | Description |
|------------|-------------|
| `config show` | Show current CLI configuration. |
| `config set <key> <value>` | Set a configuration value. |
| `config init` | Create a default `config.json`. |
| `config paths` | Show resolved paths for local CLI files. |

```sh
tx config show
tx config set noColor true
```

## `profile` — named connection profiles

```
tx profile <list|show|set|remove>
```

| Subcommand | Description |
|------------|-------------|
| `profile list` | List all saved connection profiles. |
| `profile show <name>` | Show details of a profile. |
| `profile set <name>` | Create or update a profile. `--from-active` seeds it from the active connection (explicit `-s`/`-d`/`--model`/`--auth` still win). |
| `profile remove <name>` | Delete a profile. |

```sh
tx profile set dev -s DevWorkspace -d Sales
tx profile set dev --from-active
tx connect --profile dev
```

## `init` — scaffold a new model

```
tx init [output-path] [options]
```

| Option | Description |
|--------|-------------|
| `--name <name>` | Model/database name (default: directory name). |
| `--serialization <tmdl\|bim\|pbip>` | On-disk format (default: `tmdl`). |
| `--compatibility-mode <mode>` | `PowerBI` (default) or `AnalysisServices`. |
| `--compat <level>` | Compatibility level (default: 1702 for PowerBI, 1500 otherwise). |
| `--force` | Replace anything already at the target path. |

```sh
tx init ./my-model
tx init ./my-model --serialization pbip
```

## `completion` — shell completion

```
tx completion [bash|zsh|fish|powershell]
```

```sh
tx completion bash >> ~/.bashrc
tx completion powershell | Invoke-Expression
```

## `stage` — staged mutations

```
tx stage [status|list|commit|discard]
```

Inspect and manage staged (uncommitted) model mutations — see
[Editing & staging](../guides/editing.md) for the workflow.

| Subcommand | Description |
|------------|-------------|
| `stage status` | Show staged mutations for the active model (the default). |
| `stage list` | List all staged models in the current session. |
| `stage commit` | Promote staged mutations onto the source (and workspace mirror). |
| `stage discard` | Discard staged mutations without committing them. |

```sh
tx stage             # staged mutations for the active model
tx stage commit      # promote them onto the source (and workspace mirror)
tx stage discard
```

## `update` — self-update

```
tx update [--check] [--version <v>]
```

Updates tx to the latest GitHub release. Detects how tx was installed:
a dotnet global tool runs `dotnet tool update -g Tomix.Cli`; a standalone
binary (install.sh/install.ps1) downloads the release asset, verifies it
against the published `checksums.txt`, and swaps the binary in place.

| Option | Description |
|--------|-------------|
| `--check` | Preview only: latest version plus release notes for every version between installed and latest, with `[breaking]` flags. Exits 0 whether or not an update exists — scripts should read `updateAvailable` from `--output-format json`. |
| `--version <v>` | Update (or downgrade, with `--yes`) to a specific released version. |

The binary swap always asks for confirmation; pass `--yes` to skip (required
when no TTY is available). Breaking releases are flagged from conventional-commit
`!` markers and "breaking change" phrases in the release notes, plus any
major-version bump.

```sh
tx update --check
tx update
tx update --version 0.2.0 --yes
```

Related: the throttled update notice printed after commands can be disabled
with `tx config set updateCheck false` or `TOMIX_NO_UPDATE_CHECK=1`.
