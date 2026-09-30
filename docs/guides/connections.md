# Connections & sessions

Almost every command needs a model to work against. There are three ways to
provide one, in order of precedence:

1. **Explicitly, per command** — a trailing `[model]` argument or `-m/--model`
   for a local path, or `-s/--server` + `-d/--database` for a deployed model.
2. **The active connection** — set once with `tx connect`, used by every
   subsequent command run in the same repository or folder (see [Sessions](#sessions)).
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

### Power BI Desktop

`tx connect --local` finds running Desktop instances and connects without a
token. Both the Microsoft Store and MSI/Download Center installs are detected.
A report must be open — Desktop only starts its local Analysis Services engine
once a report is loaded, and instances that have since closed are skipped.

The connection is stored as the instance's `localhost:<port>` endpoint. Desktop
picks a new port each time it starts, so re-run `tx connect --local` after
restarting it.

`tx connect` shows which report you are on:

```text
Active: Markedsdata  (localhost:50987)
```

The name is remembered from when you connected and rechecked each time it is
shown, so it disappears rather than going stale — whether that report was closed
or a different instance has since taken the port. Connecting to an endpoint
directly (`tx connect localhost:50987`) skips discovery, so no name is recorded.

With more than one report open you get a picker, labelled by report name:

```text
Select a Power BI Desktop instance:
  > Sales Overview      (localhost:59962)
    Finance Monthly     (localhost:60415)
```

Off a TTY the instances are listed instead, so you can connect to one directly:
`tx connect localhost:59962`. Note a Desktop instance cannot be selected by
model name — over XMLA its database is named by a GUID and its model is always
literally `Model`, so the report name (the Desktop window title) is the only
label that distinguishes them.

```sh
tx connect                    # show the current connection
tx connect --clear            # forget it
tx connect --recent           # pick from recently used models
```

## Sessions

The active connection is scoped to the directory you run `tx` from: the
enclosing git repository root (each git worktree counts as its own root), or
the current folder when you are not inside a repository. Every terminal and
agent working in the same repository shares one connection. A different
repository or worktree starts with no connection, so it never picks up a model
you connected to somewhere else.

To share one connection across directories, or to keep two terminals in the
same repository apart, name the session explicitly:

```sh
export TOMIX_SESSION=sales     # PowerShell: $env:TOMIX_SESSION = "sales"
```

Whenever a command picks up its model from the active connection rather than
an argument, it names that model on stderr (hidden by `--quiet` and in JSON/CSV
output), so you always see what you are operating on:

```text
Connected to: Sales Overview  (localhost:50987)
```

`tx connect` with no arguments shows the connection and, on its last line, the
session it belongs to (the repository or folder, or the `TOMIX_SESSION` name).
With `--output-format json` the same details are under `session`: `id`,
`kind`, `scope`, and the session file's `path`.

```sh
tx connect                # show the connection and its session
tx connect --clear        # forget the connection in this session
tx connect --clear --all  # forget it in every session
```

Session files clean up after themselves: each `tx connect <target>` removes
the files of sessions whose folder no longer exists (a deleted worktree or
clone). `tx doctor` reports any that are left.

## Authentication

Remote targets authenticate via `tx auth`:

```sh
tx auth login                              # interactive browser login
tx auth login --device-code                # no local browser (SSH, containers)
tx auth login -u $APP_ID --tenant $TENANT --password-file ./secret.txt
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
printf '%s' "$SECRET" | tx auth login -u $APP_ID --tenant $TENANT --password -
```

Saved credentials renew silently on Windows, macOS, and Linux. See the
[`auth` reference](../commands/connect.md#auth-authentication) for all
options, including managed identity (`--identity`).

## Profiles

Named profiles capture a connection for quick environment switching:

```sh
tx profile set dev -s DevWorkspace -d Sales
tx profile set desktop --from-active   # preserves Desktop Local mode
tx profile list
tx connect --profile dev        # activate it
tx deploy --profile prod        # or use one-shot, without persisting
```

`--from-active` also preserves workspace mirroring; explicit connection flags
override the copied active values. Profile activation validates the resolved
local path or remote database before replacing the active session.

## Workspace mode

`-w/--workspace` mirrors saves between a primary source and a secondary
target — for example, edit a local TMDL folder and have every committed
mutation synced to a deployed workspace copy (or the reverse):

```sh
tx connect ./model.tmdl -w MyWorkspace Sales   # local primary, remote mirror
tx connect MyWorkspace Sales -w ./model.tmdl   # remote primary, local mirror
tx connect ./model.tmdl -w                     # pick the target interactively
```

A sync overwrites the mirror with the primary model, because the primary is the
edited copy and preserving target objects would silently revert the edit. The
one exception is incremental-refresh policy partitions: they are generated and
processed on the service, so the mirror keeps them — and their processed data —
even though the local model has none. `tx set <table>/RefreshPolicy` and `tx rm <table>/RefreshPolicy` are exempt from
the exception: when they edit a refresh policy, the sync replaces the policy and
its partitions in full, so the change actually lands. To force a complete
overwrite of everything, deploy explicitly:

```sh
tx deploy -s MyWorkspace -d Sales --deploy-full
```

Individual commands can skip the mirror with `--no-sync`. The mirror only
applies to the session's primary model: a command addressed at an explicit
source (a model path or `-s`/`-d`) that resolves to something other than the
primary never uses the mirror — neither syncing a save to it nor falling back
to it for refresh, query, or statistics.

## Non-interactive contexts

In scripts and CI, pass `--non-interactive`: every prompt is disabled and
missing input fails with an actionable error instead of hanging.
