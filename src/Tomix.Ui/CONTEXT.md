# Tomix.Ui

The localhost web endpoint of a shared live session (docs/protocol.md, Transport), on ASP.NET
Core (Kestrel), as decided in #356.

## Responsibilities

- Listen on `127.0.0.1` only: `/ws` for protocol clients over a WebSocket, `GET /status` for
  tools that poll.
- Refuse every request without the session token, with a `Host` other than this server, or with
  an `Origin` other than its own (`TOMIX_UI_*` answers).
- Later: serve the built web app (`apps/web`, #361).

## Cross-folder dependencies

- Depends on nothing else in the repo; it takes the session through `UiHostOptions` callbacks
  (`Status`, `Connect`), so it knows nothing of sessions, commands or the protocol's methods.
- Referenced by `/src/Tomix.Cli`, whose `Serve/WebEndpoint` plugs a `SessionHost` into it.
- Must not be referenced by `/src/Tomix.Core`, `/src/Tomix.Platform`, `/src/Tomix.App` or
  provider projects: they stay free of web dependencies.

## Rules

- Stay Native AOT compatible (#329): `CreateSlimBuilder`, a single `RequestDelegate` instead of
  reflection-bound minimal API handlers, no reflection-based JSON.
- Never write to stdout: logging providers are cleared, and the host does not handle Ctrl+C; the
  command that starts it does.
- Keep security checks in `UiHost.HandleAsync`, before any routing.

## Test

Driven through an in-memory test server (`UseTestServer`), so tests open no sockets:

```bash
dotnet test tests/Tomix.Cli.Tests --filter SharedSessionTests
```
