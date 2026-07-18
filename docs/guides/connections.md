# Connections & sessions

Almost every command needs a model to work against. There are three ways to
provide one, in order of precedence:

1. **Explicitly, per command** — a trailing `[model]` argument or `-m/--model`
   for a local path, or `-s/--server` + `-d/--database` for a deployed model.
2. **The active connection** — set once with `tx connect`, used by every
   subsequent command in the same terminal session.
3. **Recents** — `--recent` reconnects to a recently used model (no value =
   interactive picker, `N` = Nth most recent).

## Connection targets

`tx` speaks to four kinds of targets:

| Target | Example |
|--------|---------|
| TMDL folder | `tx connect ./model.tmdl` |
| `.bim` file | `tx connect ./model.bim` |
| XMLA endpoint / workspace | `tx connect MyWorkspace Sales` |
| Power BI Desktop (Windows only) | `tx connect --local` |

On a TTY you can pick interactively: `tx connect --remote` lists workspaces
and models from your tenant; `tx connect MyWorkspace` (no database) lists that
workspace's models.

```sh
tx connect                    # show the current connection
tx connect --clear            # forget it
tx connect --recent           # pick from recently used models
```

## Sessions

The active connection is scoped to your terminal session, so two terminals can
work against two different models without interfering:

```sh
tx session          # show session ID, file path, active state
tx session clear    # clear active state for this session
tx session prune    # delete session files for dead shells
```

## Authentication

Remote targets authenticate via `tx auth`:

```sh
tx auth login                              # interactive browser login
tx auth login --device-code                # no local browser (SSH, containers)
tx auth login -u $APP_ID -t $TENANT --password-file ./secret.txt
tx auth status
tx auth logout
```

The `--auth` global option selects the method per command: `auto` (default),
`interactive`, `spn`, or `managed-identity`.

Secrets never travel on the command line or in environment variables — plain
secret values as arguments are rejected. `tx auth login` takes a
service-principal secret from a masked prompt, a file (`--password-file`), or
stdin (`--password -`); certificate auth (`--certificate`) follows the same
pattern. In CI:

```sh
printf '%s' "$SECRET" | tx auth login -u $APP_ID -t $TENANT --password -
```

Saved credentials renew silently on Windows, macOS, and Linux. See the
[`auth` reference](../commands/connect.md#auth-authentication) for all
options, including managed identity (`--identity`).

## Profiles

Named profiles capture a connection for quick environment switching:

```sh
tx profile set dev -s DevWorkspace -d Sales
tx profile list
tx connect --profile dev        # activate it
tx deploy --profile prod        # or use one-shot, without persisting
```

## Workspace mode

`-w/--workspace` mirrors saves between a primary source and a secondary
target — for example, edit a local TMDL folder and have every committed
mutation synced to a deployed workspace copy (or the reverse):

```sh
tx connect ./model.tmdl -w MyWorkspace Sales   # local primary, remote mirror
tx connect MyWorkspace Sales -w ./model.tmdl   # remote primary, local mirror
tx connect ./model.tmdl -w                     # pick the target interactively
```

Individual commands can skip the mirror with `--no-sync`.

## Non-interactive contexts

In scripts and CI, pass `--non-interactive`: every prompt is disabled and
missing input fails with an actionable error instead of hanging.
