# Connect

Commands for working with connections and deployed models. The connection
model itself — sessions, profiles, workspace mode — is explained in
[Connections & sessions](../guides/connections.md).

## `connect` — set the active connection

```
tx connect [server] [database] [options]
```

No arguments shows the current connection. The first argument can be a
workspace name, an endpoint, or a local model path.

| Option | Description |
|--------|-------------|
| `--local` | Connect to a locally running Power BI Desktop instance (Windows only). |
| `--remote` | Pick a workspace and model interactively from your tenant (requires a TTY; sign in first with `tx auth login`). |
| `-p, --profile <name>` | Activate a saved connection profile. |
| `--clear` | Clear the active connection. |
| `-w, --workspace [target]` | Enable workspace mode: mirror saves between the primary source and a secondary target. No value = pick interactively. |
| `--workspace-format <fmt>` | On-disk format for a local workspace target (`tmdl`, `bim`, `te-folder`). |
| `--workspace-auth <auth>` | Auth method for a remote workspace target. |
| `--force` | Overwrite a non-empty workspace target when initializing workspace mode. |

```sh
tx connect                          # show current
tx connect --remote
tx connect MyWorkspace Sales
tx connect ./model.tmdl
tx connect --local                  # Power BI Desktop (Windows only)
tx connect ./model.tmdl -w MyWorkspace Sales
```

## `deploy` — deploy to a workspace

```
tx deploy [model] [options]
```

Runs the BPA gate before deploying (configured via `.te-bpa.json`).

| Option | Description |
|--------|-------------|
| `--dry-run` | Preview what would change on the remote target. |
| `--xmla <file>` | Generate the XMLA/TMSL script to a file instead of deploying (`-` for stdout). |
| `--create-only` | Only create a new model; fail if it already exists. |
| `--skip-bpa` / `--fix-bpa` | Skip the BPA gate, or auto-fix violations before deploying. |
| `--bpa-rules <file>` | BPA rule file(s) for this deploy. |
| `-p, --profile <name>` | Use a saved profile for this deploy only. |
| `--ci <github\|vsts>` | Emit CI logging commands to stderr. |
| `--force` | Bypass validation checks. |

```sh
tx deploy ./model.tmdl --dry-run
tx deploy --server MyWorkspace --database Sales
tx deploy ./model.bim --xmla deploy.xmla
```

## `refresh` — trigger a data refresh

```
tx refresh [options]
```

| Option | Description |
|--------|-------------|
| `--type <type>` | `full`, `dataonly`, `automatic` (default), `calculate`, `clearvalues`, `defragment`, `add`. |
| `--table <name>` | Refresh specific table(s). Repeatable. |
| `--partition <Table.Partition>` | Refresh specific partition(s). Repeatable. |
| `--apply-refresh-policy [true\|false]` / `--skip-refresh-policy` | Apply incremental refresh policy (default: `true`); `--skip-refresh-policy` is shorthand for `--apply-refresh-policy false`. |
| `--effective-date <yyyy-MM-dd>` | Override the current date for refresh-policy evaluation. |
| `--max-parallelism <n>` | Maximum parallel refresh operations. |
| `--dry-run` | Output the TMSL script without executing it. |
| `--no-progress` | Disable live progress tracking (for CI/piping). |
| `--trace [path]` | Dump raw XMLA trace events (stderr, or a log file). |

```sh
tx refresh --type full
tx refresh --table Sales --table Customers
```

## `load` — load and summarize

```
tx load [model]
```

Loads a model and prints a summary — useful as a smoke test or, with
`--output-format json`, as a machine-readable model inventory.

## `save` — export a model

```
tx save [model] [options]
```

| Option | Description |
|--------|-------------|
| `-o, --output-path <path>` | Where to write. Omit to save back to the source. |
| `--serialization <tmdl\|bim>` | Output format (defaults to the loaded model's). |
| `--supporting-files` | Wrap output in a `{modelName}.SemanticModel/` folder with `.platform` and `definition.pbism`. |
| `--fix-bpa` / `--bpa-rules <file>` | Auto-fix BPA violations before saving, optionally with specific rule files. |
| `--force` | Skip validation and overwrite existing output. |

```sh
tx save -s MyWorkspace -d Sales -o ./sales.tmdl          # download a deployed model
tx save ./model.tmdl --serialization bim -o ./model.bim  # convert formats
```

## `auth` — authentication

```
tx auth <login|logout|status>
```

`auth login` options:

| Option | Description |
|--------|-------------|
| `-u, --username <id>` | Service-principal application (client) id. |
| `-p, --password <source>` | Service-principal client secret source: pass `-` to read one line from stdin. Secret values on the command line are rejected. |
| `--password-file <file>` | Path to a file containing the client secret (trailing newline ignored). |
| `-t, --tenant <tenant>` | Tenant id or domain (required for service principal). |
| `--certificate <file>` | Certificate file (PEM or PKCS12) for service-principal auth. |
| `--certificate-password <source>` / `--certificate-password-file <file>` | Certificate password via stdin (`-`) or file; plain values on the command line are rejected. |
| `-I, --identity` | Sign in with a managed identity (Azure-hosted; use `--username` for user-assigned). |
| `--device-code` | Use the device-code flow instead of a local browser. |
| `--client-id <id>` | Override the Azure AD client id used for interactive/device-code sign-in. |
| `--save` | Persist service-principal credentials for silent reuse (default: true). `--save false` for one-shot login. |

```sh
tx auth login                          # interactive browser login
tx auth login --device-code            # no local browser (SSH, containers)

# Service principal — secret from stdin (CI) or a file, never argv
printf '%s' "$SECRET" | tx auth login -u $APP_ID -t $TENANT --password -
tx auth login -u $APP_ID -t $TENANT --password-file ./secret.txt

tx auth status
tx auth logout
```

## `session` — terminal session state

```
tx session [show|clear|list|prune]
```

| Subcommand | Description |
|------------|-------------|
| `session show` | Show current session details (ID, file path, active state). |
| `session clear` | Clear active state for the current session. |
| `session list` | List all session files. |
| `session prune` | Delete session files whose shell process is no longer running. |

`session prune` options:

| Option | Description |
|--------|-------------|
| `--all` | Also remove named and live process sessions. The current session is kept. |
| `--dry-run` | Show what would be removed without doing it. |

`session clear` and `session prune` ask for confirmation (`--dry-run` never
does); pass `--yes` in scripts.

```sh
tx session            # current session details
tx session clear      # clear active state for this session
tx session prune      # delete session files for dead shells
tx session prune --all --dry-run
```
