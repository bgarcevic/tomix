# Session protocol (v0)

The tomix session protocol lets other programs work on a model that tomix holds
open: editors, the tomix UI, and AI agents through `tx mcp`. Every client
attached to a session sees the same model, the same undo history and the same
change events as `tx interactive`.

This page is the v0 specification. `tx serve` implements it on stdin and
stdout ([`tx serve`](commands/modify.md#serve-serve-a-session-to-other-programs)).
A localhost WebSocket carries the same messages to several clients of one
session; `tx ui` opens it (#369). The design behind it is
[ADR 0001](design/adr-0001-live-model-session.md).

`tx serve` does not serve `query.run`, `$/progress` or `diagnostics.updated`
yet. `initialize` lists what it does serve, so a client can check
`capabilities` rather than this page.

## Transport

Messages are [JSON-RPC 2.0](https://www.jsonrpc.org/specification) on either
transport.

### stdio

- JSON-RPC over the server's stdin and stdout. stderr carries logs only and is
  never part of the protocol.
- Each message is framed like the
  [Language Server Protocol](https://microsoft.github.io/language-server-protocol/specifications/base/0.9/specification/):
  a `Content-Length` header giving the body's length in bytes, a blank line,
  then the UTF-8 JSON body.

  ```text
  Content-Length: 59\r\n
  \r\n
  {"jsonrpc":"2.0","id":1,"method":"session.status"}
  ```

- Batch requests (a JSON array of messages) are not supported in v0 and are
  answered with error `-32600`.
- The client may send several requests without waiting for answers. The server
  answers each one; answers can arrive in a different order from the requests.

### WebSocket

A session shared by several clients (the browser UI, agents, other tools)
listens on `ws://127.0.0.1:<port>/ws`.

- One JSON-RPC message per WebSocket text message, with no `Content-Length`
  header. Binary messages are answered with error `-32600`.
- Each connection is one client, with its own `initialize`, `clientId`,
  requests and transaction. Every client receives every session event.
- The server listens on `127.0.0.1` only. Every request must:
  - carry the session token, as `Authorization: Bearer <token>` or, from a
    browser, which cannot set headers on a WebSocket, as `?token=<token>`;
  - name the server in `Host` (`127.0.0.1:<port>` or `localhost:<port>`);
  - send no `Origin`, or the server's own (`http://127.0.0.1:<port>`).

  A request without the token is answered with HTTP 401 and
  `TOMIX_UI_UNAUTHORIZED`; one naming another host or origin with 403 and
  `TOMIX_UI_FORBIDDEN`.

### Status endpoint

`GET http://127.0.0.1:<port>/status` answers a small JSON object for tools
that poll, such as a status bar. It needs the token like every other request.
It reads counters the session already keeps and never waits for the model, so
polling it every second is fine. Fields may be added; none are renamed or
removed within a protocol version.

```json
{
  "protocolVersion": "0",
  "model": "C:/models/Sales",
  "state": "dirty",
  "dirty": true,
  "version": 42,
  "undoSteps": 7,
  "redoSteps": 0,
  "transaction": { "id": "t18", "client": "mcp-1", "label": "rename measures" },
  "clients": ["tomix-ui-1", "mcp-1"],
  "lastChange": { "version": 42, "client": "mcp-1", "at": "2026-10-05T14:03:11Z" }
}
```

- `state` is a session state (`clean`, `dirty`, …), or `closed` when no model is
  open.
- `transaction` is the open explicit transaction, or `null`.
- `clients` lists the connected clients that have sent `initialize`.
- `lastChange` is the last `model.changed` batch since the model was opened, or
  `null`.

### Discovery

A process that shares a session writes one file per open model to
`~/.tomix/live/` (only the current user can read it):

```json
{
  "model": "C:/models/Sales",
  "pid": 18244,
  "port": 51873,
  "url": "http://127.0.0.1:51873/",
  "token": "q3V…",
  "startedAt": "2026-10-05T14:00:02Z"
}
```

The file is removed when the process ends. A file whose `pid` is no longer
running is stale: ignore it (tx deletes such files when it finds them). No
file, or a refused connection, means no session is open.

## Lifecycle

1. The client sends `initialize`. Until it has an answer, any other request
   fails with `-32002` (not initialized).
2. The client works with the session: requests, and notifications from the
   server.
3. The client sends `shutdown`, waits for its answer, then sends the `exit`
   notification. The server exits with code 0 after a `shutdown`, and 1 if
   `exit` arrives without one.

A client that disconnects without `shutdown` detaches. It never closes the
session (only the host does), and an explicit transaction it left open is
rolled back; the other clients receive `transaction.closed` with outcome
`rolledBack`.

## Conventions

### Method names

Methods are named `area.verb`: `session.open`, `object.set`, `model.changed`.
Names starting with `$/` belong to the protocol itself (`$/cancelRequest`,
`$/progress`). A server ignores a `$/` notification it does not know, and
answers an unknown `$/` request with `-32601`.

### Requests and results

- Parameters use the same camelCase names as the matching CLI command's JSON.
  `object.set` takes the `path` and the property assignments `tx set` takes,
  for example. The model is always the session's, so no request names one,
  and the `--save`, `--stage` and `--revert` choices of the CLI do not exist:
  every edit applies to the session, and `session.save` persists it.
- A result is the same envelope the CLI writes with `--output-format json`,
  plus the session version it reflects:

  ```json
  { "data": { "...": "..." }, "diagnostics": [], "version": 42 }
  ```

  `data` has exactly the shape of the command's `data`, so a client can share
  code between the CLI and the protocol. `diagnostics` holds warnings.
  `version` is the session version after the request; a read reports the
  version it read.

### Addressing objects

Every object in a session has an ID, an opaque string such as `o1k3` that stays
the same through renames and moves and is never reused within the session.
Requests that take an object accept either `path` or `id`, never both:

```json
{ "path": "Sales/Total Sales" }
```

```json
{ "id": "o1k3" }
```

IDs do not survive the session: store paths (or lineage tags), not IDs.

### Transactions

Each request runs as one transaction: it applies completely or not at all, and
it is one undo step. `transaction.begin` groups the client's following
requests into one step until `transaction.commit` or `transaction.rollback`.
While a client's transaction is open, other clients' edits wait for it, and
their reads see the last committed version.

### Errors

A failed request is answered with a JSON-RPC error. Errors from tomix carry the
same code, message and hint as the CLI's
[error output](error-codes.md):

```json
{
  "jsonrpc": "2.0",
  "id": 30,
  "error": {
    "code": -32000,
    "message": "Object not found: Sales/Profit",
    "data": {
      "code": "TOMIX_OBJECT_NOT_FOUND",
      "hint": "Run 'tx get' to list the tables, or 'tx get \"Sa*\"' to filter.",
      "exitCode": 2
    }
  }
}
```

| `code` | Meaning |
|--------|---------|
| `-32700` | The body is not valid JSON. |
| `-32600` | The message is not a valid JSON-RPC request (including batches). |
| `-32601` | Unknown method. |
| `-32602` | The parameters do not match the method. `data.code` names the problem when tomix can, for example `TOMIX_INVALID_TYPE`. |
| `-32603` | Internal error (`data.code` is `TOMIX_UNEXPECTED`). |
| `-32002` | A request arrived before `initialize`. |
| `-32800` | The request was cancelled with `$/cancelRequest`. |
| `-32000` | Any other tomix error: `data.code` is its `TOMIX_*` code, `data.hint` the remediation and `data.exitCode` the exit code the CLI would use. |

Clients should branch on `data.code`, not on `message`.

### Cancellation and progress

- `$/cancelRequest` (notification, `{ "id": <request id> }`) cancels a
  request. One not yet started is dropped; an edit in progress rolls back;
  a query stops. The cancelled request is answered with error `-32800`.
- A request whose params include `"progressToken"` gets `$/progress`
  notifications with that token while it runs (a refresh, a query, a BPA run).

### Versioning

- The protocol version is `"0"`. `initialize` returns it, and the client sends
  the version it speaks.
- Additive changes stay in v0: new methods, new notifications, new optional
  parameters, new result or event fields, new `TOMIX_*` codes. Clients must
  ignore fields and notifications they do not know, and should check
  `capabilities.methods` before calling a method added later.
- Anything else (removing or renaming a method, field or value, changing a
  type or a default) is a new protocol version.

## Methods

| Method | Kind | Purpose |
|--------|------|---------|
| `initialize` | request | Agree on the protocol version; learn the server's capabilities. |
| `shutdown` | request | Detach; the next message is `exit`. |
| `exit` | notification | End the connection. |
| `session.open` | request | Open a model as the session. |
| `session.close` | request | Close the session, saving or discarding unsaved changes. |
| `session.status` | request | Model, state, unsaved changes, undo depth, open transaction. |
| `session.save` | request | Write the session to its source, or a copy elsewhere. |
| `session.snapshot` | request | The whole model at the current version, to (re)synchronize. |
| `session.history` | request | The steps undo and redo walk through. |
| `session.undo` | request | Revert the last step. |
| `session.redo` | request | Reapply the last undone step. |
| `transaction.begin` | request | Group the following requests into one undo step. |
| `transaction.commit` | request | Keep the group as one step. |
| `transaction.rollback` | request | Discard the group. |
| `model.summary` | request | Model name, format and object counts. |
| `model.tree` | request | One level of the object tree, for lazy loading. |
| `object.get` | request | One object's properties, or a listing. |
| `object.find` | request | Search names and expressions. |
| `deps.get` | request | Upstream and downstream dependencies of an object. |
| `object.add` | request | Create an object. |
| `object.set` | request | Change properties. |
| `object.remove` | request | Remove an object. |
| `object.move` | request | Rename or move an object. |
| `model.replace` | request | Find and replace across the model. |
| `bpa.run` | request | Run the Best Practice Analyzer. |
| `bpa.fix` | request | Apply the analyzer's fixes. |
| `dax.format` | request | Format an expression, or an object's. |
| `dax.check` | request | Check the model's DAX and references. |
| `query.run` | request | Run a DAX or DMV query (server-backed models). |
| `$/cancelRequest` | notification | Cancel a request. |
| `$/progress` | notification | Progress of a long request. |
| `model.changed` | notification | A transaction was committed. |
| `session.state` | notification | The session's state changed. |
| `session.saved` | notification | The session was saved. |
| `transaction.opened` | notification | A client opened a transaction. |
| `transaction.closed` | notification | That transaction was committed or rolled back. |
| `diagnostics.updated` | notification | Dependency, DAX and BPA results were recomputed. |

Reserved for later versions: `session.reload` and `session.merge` (#351,
#374), and `changeSet.*` for proposed edits that a person approves (#370).

### Lifecycle methods

#### `initialize`

`clientInfo` names the client in change events (`origin.client`); the server
returns the ID it will use for this client: the name, numbered per name in the
session (`tomix-ui-1`, `tomix-ui-2`).

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "initialize",
  "params": {
    "protocolVersion": "0",
    "clientInfo": { "name": "tomix-ui", "version": "0.1.0" }
  }
}
```

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "protocolVersion": "0",
    "serverInfo": { "name": "tx", "version": "0.8.1" },
    "clientId": "tomix-ui-1",
    "capabilities": {
      "methods": ["session.open", "session.close", "session.status", "object.get", "object.set", "session.undo"],
      "notifications": ["model.changed", "session.state", "session.saved", "diagnostics.updated"]
    }
  }
}
```

A server that does not speak the requested version answers with error
`-32000` and `data.code` `TOMIX_PROTOCOL_VERSION`, listing the versions it
speaks in `data.supported`.

#### `shutdown`

```json
{ "jsonrpc": "2.0", "id": 99, "method": "shutdown" }
```

```json
{ "jsonrpc": "2.0", "id": 99, "result": null }
```

#### `exit`

```json
{ "jsonrpc": "2.0", "method": "exit" }
```

### Session methods

#### `session.open`

Opens a model, in the same forms `tx interactive` and `tx connect` take:
`model` is a TMDL folder or `.bim` file, or `server` and `database` name a
remote model. A session already open is closed first; when it has unsaved
changes the request fails with `TOMIX_SESSION_DIRTY` unless `discard` is
`true`, and while other clients are connected it fails with
`TOMIX_SESSION_IN_USE`. The result is the new session's status.

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "session.open",
  "params": { "model": "/models/sales" }
}
```

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "result": {
    "data": {
      "model": "/models/sales",
      "source": "/models/sales",
      "state": "clean",
      "dirty": false,
      "version": 0,
      "undoSteps": 0,
      "redoSteps": 0,
      "transaction": null
    },
    "diagnostics": [],
    "version": 0
  }
}
```

#### `session.close`

Closes the session. With unsaved changes it fails with `TOMIX_SESSION_DIRTY`
unless `save` or `discard` is `true`. While other clients are connected it
fails with `TOMIX_SESSION_IN_USE`: the session is theirs too.

```json
{
  "jsonrpc": "2.0",
  "id": 3,
  "method": "session.close",
  "params": { "save": true }
}
```

```json
{ "jsonrpc": "2.0", "id": 3, "result": { "data": { "closed": true, "saved": true }, "diagnostics": [], "version": 6 } }
```

#### `session.status`

The `status` payload of `tx interactive`.

```json
{ "jsonrpc": "2.0", "id": 4, "method": "session.status" }
```

```json
{
  "jsonrpc": "2.0",
  "id": 4,
  "result": {
    "data": {
      "model": "/models/sales",
      "source": "/models/sales",
      "state": "dirty",
      "dirty": true,
      "version": 6,
      "undoSteps": 4,
      "redoSteps": 0,
      "transaction": null
    },
    "diagnostics": [],
    "version": 6
  }
}
```

#### `session.save`

The `tx save` payload. Without `outputFile` it writes back to the source and
makes the current version the save point; with it, it writes a copy and the
session keeps saving to its source. `serialization` and `fixBpa` work as in
`tx save`.

```json
{ "jsonrpc": "2.0", "id": 5, "method": "session.save", "params": {} }
```

```json
{
  "jsonrpc": "2.0",
  "id": 5,
  "result": {
    "data": {
      "format": "tmdl",
      "status": "saved",
      "saved": true,
      "savedTo": "/models/sales",
      "persistence": "file",
      "sync": { "status": "notConfigured" }
    },
    "diagnostics": [],
    "version": 6
  }
}
```

#### `session.snapshot`

Every object with its ID and path, at the current version. A client calls it
after connecting, and again whenever it sees a gap in `model.changed`
versions.

```json
{ "jsonrpc": "2.0", "id": 6, "method": "session.snapshot" }
```

```json
{
  "jsonrpc": "2.0",
  "id": 6,
  "result": {
    "data": {
      "name": "sales",
      "objects": [
        { "id": "o1", "type": "Table", "path": "Sales", "name": "Sales" },
        { "id": "o2", "type": "Column", "path": "Sales/Amount", "name": "Amount" },
        { "id": "o3", "type": "Measure", "path": "Sales/Total Sales", "name": "Total Sales" }
      ]
    },
    "diagnostics": [],
    "version": 6
  }
}
```

#### `session.history`

The `history` payload of `tx interactive`: undo steps oldest first, then the
steps redo would reapply, next one first. `label` is what made the step: the
command line in `tx interactive`, or the method name for protocol clients.

```json
{ "jsonrpc": "2.0", "id": 7, "method": "session.history" }
```

```json
{
  "jsonrpc": "2.0",
  "id": 7,
  "result": {
    "data": {
      "steps": [
        { "transaction": "t1", "label": "object.add", "changes": 1, "undone": false },
        { "transaction": "t2", "label": "object.set", "changes": 1, "undone": true }
      ]
    },
    "diagnostics": [],
    "version": 3
  }
}
```

#### `session.undo`

Reverts the last step, whoever made it. Fails with
`TOMIX_SESSION_NOTHING_TO_UNDO` when there is none, and with
`TOMIX_SESSION_IN_TRANSACTION` while the client has a transaction open.
`changes` lists what the undo changed, as in `model.changed`.

```json
{ "jsonrpc": "2.0", "id": 8, "method": "session.undo" }
```

```json
{
  "jsonrpc": "2.0",
  "id": 8,
  "result": {
    "data": {
      "action": "undo",
      "transaction": "t4",
      "label": "object.remove",
      "changes": [
        { "id": "ol", "objectKind": "Measure", "change": "added", "path": "Sales/Avg Sale" }
      ],
      "version": 5,
      "dirty": true
    },
    "diagnostics": [],
    "version": 5
  }
}
```

#### `session.redo`

Reapplies the last undone step. Fails with `TOMIX_SESSION_NOTHING_TO_REDO`
when there is none.

```json
{ "jsonrpc": "2.0", "id": 9, "method": "session.redo" }
```

```json
{
  "jsonrpc": "2.0",
  "id": 9,
  "result": {
    "data": {
      "action": "redo",
      "transaction": "t4",
      "label": "object.remove",
      "changes": [
        { "id": "ol", "objectKind": "Measure", "change": "removed", "path": "Sales/Avg Sale" }
      ],
      "version": 6,
      "dirty": true
    },
    "diagnostics": [],
    "version": 6
  }
}
```

#### `transaction.begin`

Opens a transaction for this client; `label` names it in history. Fails with
`TOMIX_SESSION_TRANSACTION_OPEN` when the client already has one. A
transaction idle for 15 minutes is rolled back.

```json
{ "jsonrpc": "2.0", "id": 10, "method": "transaction.begin", "params": { "label": "Margin measures" } }
```

```json
{
  "jsonrpc": "2.0",
  "id": 10,
  "result": {
    "data": { "action": "begin", "transaction": "t7", "label": "Margin measures", "changes": [], "version": 6, "dirty": true },
    "diagnostics": [],
    "version": 6
  }
}
```

#### `transaction.commit`

Keeps the transaction's requests as one undo step and publishes one
`model.changed`. Fails with `TOMIX_SESSION_NO_TRANSACTION` when none is open,
and with `TOMIX_SESSION_TRANSACTION_ENDED` when the session already rolled it
back.

```json
{ "jsonrpc": "2.0", "id": 13, "method": "transaction.commit" }
```

```json
{
  "jsonrpc": "2.0",
  "id": 13,
  "result": {
    "data": {
      "action": "commit",
      "transaction": "t7",
      "label": "Margin measures",
      "changes": [
        { "id": "o2a", "objectKind": "Measure", "change": "added", "path": "Sales/Margin" },
        { "id": "o2b", "objectKind": "Measure", "change": "added", "path": "Sales/Margin %" }
      ],
      "version": 7,
      "dirty": true
    },
    "diagnostics": [],
    "version": 7
  }
}
```

#### `transaction.rollback`

Discards everything the transaction did.

```json
{ "jsonrpc": "2.0", "id": 14, "method": "transaction.rollback" }
```

```json
{
  "jsonrpc": "2.0",
  "id": 14,
  "result": {
    "data": { "action": "rollback", "transaction": "t8", "label": null, "changes": [], "version": 7, "dirty": true },
    "diagnostics": [],
    "version": 7
  }
}
```

### Reading the model

#### `model.summary`

The `tx summary` payload.

```json
{ "jsonrpc": "2.0", "id": 15, "method": "model.summary" }
```

```json
{
  "jsonrpc": "2.0",
  "id": 15,
  "result": {
    "data": {
      "name": "sales",
      "source": "/models/sales",
      "format": "tmdl",
      "compatibilityLevel": 1601,
      "culture": "en-US",
      "defaultMode": "Import",
      "counts": {
        "tables": 3, "columns": 12, "measures": 4, "relationships": 2, "roles": 0,
        "partitions": 3, "calculationGroups": 0, "perspectives": 0, "cultures": 0
      }
    },
    "diagnostics": [],
    "version": 6
  }
}
```

#### `model.tree`

One level of the object tree, for a tree view that loads children on demand.
Without `path` or `id` it returns the model's top level (tables, relationships,
roles, ...). `hasChildren` says whether to show an expander.

```json
{ "jsonrpc": "2.0", "id": 16, "method": "model.tree", "params": { "path": "Sales" } }
```

```json
{
  "jsonrpc": "2.0",
  "id": 16,
  "result": {
    "data": {
      "parent": { "id": "o1", "path": "Sales" },
      "children": [
        { "id": "o2", "type": "Column", "path": "Sales/Amount", "name": "Amount", "hasChildren": false },
        { "id": "o3", "type": "Measure", "path": "Sales/Total Sales", "name": "Total Sales", "hasChildren": false },
        { "id": "o9", "type": "Partition", "path": "Sales/Partitions/Sales", "name": "Sales", "hasChildren": false }
      ]
    },
    "diagnostics": [],
    "version": 6
  }
}
```

#### `object.get`

The `tx get` payload, with the same parameters: one object (`path` or `id`), or
a listing with `ls`, `where` and `type`. Results add each object's `id`.

```json
{ "jsonrpc": "2.0", "id": 17, "method": "object.get", "params": { "path": "Sales/Total Sales" } }
```

```json
{
  "jsonrpc": "2.0",
  "id": 17,
  "result": {
    "data": {
      "id": "o3",
      "type": "Measure",
      "path": "Sales/Total Sales",
      "properties": {
        "name": "Total Sales",
        "description": "",
        "isHidden": false,
        "expression": "SUM ( Sales[Amount] )",
        "formatString": "",
        "displayFolder": "",
        "lineageTag": ""
      }
    },
    "diagnostics": [],
    "version": 6
  }
}
```

#### `object.find`

The `tx find` payload.

```json
{ "jsonrpc": "2.0", "id": 18, "method": "object.find", "params": { "pattern": "Amount" } }
```

```json
{
  "jsonrpc": "2.0",
  "id": 18,
  "result": {
    "data": {
      "pattern": "Amount",
      "matchCount": 2,
      "matches": [
        { "objectPath": "Sales/Amount", "objectType": "Column", "property": "Name", "matchedText": "Amount", "line": 1, "position": 1 },
        { "objectPath": "Sales/Total Sales", "objectType": "Measure", "property": "Expression", "matchedText": "Amount", "line": 1, "position": 13 }
      ]
    },
    "diagnostics": [],
    "version": 6
  }
}
```

#### `deps.get`

The `tx deps` payload. `direction` is `upstream`, `downstream` or `both` (the
default); `unused` lists objects nothing depends on instead.

```json
{ "jsonrpc": "2.0", "id": 19, "method": "deps.get", "params": { "path": "Sales/Total Sales" } }
```

```json
{
  "jsonrpc": "2.0",
  "id": 19,
  "result": {
    "data": {
      "path": "Sales/Total Sales",
      "objectType": "Measure",
      "upstream": [
        { "objectName": "'Sales'[Amount]", "objectType": "Column", "path": "Sales/Amount" }
      ],
      "downstream": []
    },
    "diagnostics": [],
    "version": 6
  }
}
```

### Editing the model

Every edit applies to the session at once, as in `tx interactive`, so `status`
is `applied`. A rename rewrites the DAX that refers to the object unless
`fixRefs` is `false`, exactly as `tx set` and `tx mv` do.

#### `object.add`

The `tx add` parameters: `path`, `type`, `expression` and `set` (property
assignments).

```json
{
  "jsonrpc": "2.0",
  "id": 20,
  "method": "object.add",
  "params": { "path": "Sales/Margin", "type": "Measure", "expression": "[Total Sales] * 0.1" }
}
```

```json
{
  "jsonrpc": "2.0",
  "id": 20,
  "result": {
    "data": { "added": "Sales/Margin", "status": "applied", "saved": false, "sync": { "status": "notAttempted" } },
    "diagnostics": [],
    "version": 7
  }
}
```

#### `object.set`

`set` maps property names (as `tx get` prints them) to values.

```json
{
  "jsonrpc": "2.0",
  "id": 21,
  "method": "object.set",
  "params": { "path": "Sales/Margin", "set": { "formatString": "0.0%" } }
}
```

```json
{
  "jsonrpc": "2.0",
  "id": 21,
  "result": {
    "data": {
      "set": "Sales/Margin",
      "property": "formatString",
      "value": "0.0%",
      "validationErrors": 0,
      "status": "applied",
      "saved": false,
      "sync": { "status": "notAttempted" }
    },
    "diagnostics": [],
    "version": 8
  }
}
```

#### `object.remove`

Fails with `TOMIX_RM_BREAKS_REFS` when other objects refer to it, unless
`force` is `true`.

```json
{ "jsonrpc": "2.0", "id": 22, "method": "object.remove", "params": { "path": "Sales/Avg Sale" } }
```

```json
{
  "jsonrpc": "2.0",
  "id": 22,
  "result": {
    "data": { "removed": "Sales/Avg Sale", "status": "applied", "saved": false, "sync": { "status": "notAttempted" } },
    "diagnostics": [],
    "version": 9
  }
}
```

#### `object.move`

Renames or moves `path` (or `id`) to `to`. The object keeps its ID.

```json
{
  "jsonrpc": "2.0",
  "id": 23,
  "method": "object.move",
  "params": { "path": "Sales/Margin", "to": "Sales/Margin %" }
}
```

```json
{
  "jsonrpc": "2.0",
  "id": 23,
  "result": {
    "data": { "moved": "Sales/Margin", "to": "Sales/Margin %", "status": "applied", "saved": false, "sync": { "status": "notAttempted" } },
    "diagnostics": [],
    "version": 10
  }
}
```

#### `model.replace`

The `tx replace` parameters (`pattern`, `replacement`, `regex`,
`caseSensitive`, `in`, `type`). All matches change in one transaction.

```json
{
  "jsonrpc": "2.0",
  "id": 24,
  "method": "model.replace",
  "params": { "pattern": "Amount", "replacement": "Amt" }
}
```

```json
{
  "jsonrpc": "2.0",
  "id": 24,
  "result": {
    "data": {
      "pattern": "Amount",
      "replacement": "Amt",
      "changeCount": 1,
      "previews": [
        { "objectPath": "Sales/Total Sales", "property": "Expression", "before": "SUM ( Sales[Amount] )", "after": "SUM ( Sales[Amt] )" }
      ],
      "status": "applied",
      "saved": false,
      "sync": { "status": "notAttempted" }
    },
    "diagnostics": [],
    "version": 11
  }
}
```

### Analysis

#### `bpa.run`

The `tx bpa run` payload, without fixes. `rules` adds rule files and `rule`
limits the run to some rule IDs, as on the CLI.

```json
{ "jsonrpc": "2.0", "id": 25, "method": "bpa.run", "params": { "progressToken": "bpa-1" } }
```

```json
{
  "jsonrpc": "2.0",
  "id": 25,
  "result": {
    "data": {
      "rulesEvaluated": 26,
      "violations": 1,
      "remaining": 1,
      "ruleErrors": 0,
      "fixesApplied": 0,
      "results": [
        {
          "ruleId": "MODEL_SHOULD_HAVE_A_DATE_TABLE",
          "ruleName": "[Performance] Model should have a date table",
          "category": "Performance",
          "severity": 2,
          "severityLabel": "Warning",
          "objectName": "Model",
          "objectType": "Model",
          "objectPath": "Model",
          "description": "Generally speaking, models should generally have a date table.",
          "canFix": false
        }
      ],
      "status": "unchanged",
      "saved": false
    },
    "diagnostics": [],
    "version": 11
  }
}
```

#### `bpa.fix`

`bpa.run` with fixes applied to the session, as one transaction.
`allowDelete` permits fixes that remove objects.

```json
{ "jsonrpc": "2.0", "id": 26, "method": "bpa.fix", "params": { "allowDelete": false } }
```

```json
{
  "jsonrpc": "2.0",
  "id": 26,
  "result": {
    "data": {
      "rulesEvaluated": 26,
      "violations": 3,
      "remaining": 1,
      "ruleErrors": 0,
      "fixesApplied": 2,
      "results": [],
      "status": "applied",
      "saved": false
    },
    "diagnostics": [],
    "version": 12
  }
}
```

#### `dax.format`

The `tx format` payload. With `expression` it formats that text and changes
nothing; with `path` (or `id`) it formats the object's expression in the
session; with neither, every expression in the model. `lang` is `dax`
(default) or `m`.

```json
{ "jsonrpc": "2.0", "id": 27, "method": "dax.format", "params": { "path": "Sales/Total Sales" } }
```

```json
{
  "jsonrpc": "2.0",
  "id": 27,
  "result": {
    "data": {
      "success": true,
      "path": "Sales/Total Sales",
      "language": "dax",
      "formatStatus": "unchanged",
      "formatted": "SUM ( Sales[Amount] )",
      "status": "unchanged",
      "saved": false
    },
    "diagnostics": [],
    "version": 12
  }
}
```

#### `dax.check`

The `tx validate` payload: DAX syntax and broken references across the model.
The host also runs it after every change and announces new results with
`diagnostics.updated`.

```json
{ "jsonrpc": "2.0", "id": 28, "method": "dax.check" }
```

```json
{
  "jsonrpc": "2.0",
  "id": 28,
  "result": {
    "data": { "modelName": "sales", "valid": true, "durationMs": 191, "errors": [], "warnings": [] },
    "diagnostics": [],
    "version": 12
  }
}
```

#### `query.run`

The `tx query` payload, for server-backed sessions. `maxRows` caps the rows
returned (`truncated` says whether it did). Cancel it with `$/cancelRequest`.

```json
{
  "jsonrpc": "2.0",
  "id": 29,
  "method": "query.run",
  "params": { "query": "EVALUATE TOPN(2, Sales)", "maxRows": 1000, "progressToken": "q-1" }
}
```

```json
{
  "jsonrpc": "2.0",
  "id": 29,
  "result": {
    "data": {
      "server": "powerbi://api.powerbi.com/v1.0/myorg/Finance",
      "database": "Sales",
      "columns": [
        { "name": "Sales[Amount]", "type": "decimal" },
        { "name": "Sales[Date]", "type": "dateTime" }
      ],
      "rows": [
        [120.5, "2024-01-03T00:00:00"],
        [80, "2024-01-04T00:00:00"]
      ],
      "rowCount": 2,
      "truncated": false,
      "durationMs": 231
    },
    "diagnostics": [],
    "version": 12
  }
}
```

### Protocol notifications

#### `$/cancelRequest`

```json
{ "jsonrpc": "2.0", "method": "$/cancelRequest", "params": { "id": 29 } }
```

#### `$/progress`

`message` is what the CLI's spinner or progress bar would show; `percentage`
is present when the work is measurable.

```json
{
  "jsonrpc": "2.0",
  "method": "$/progress",
  "params": { "token": "bpa-1", "message": "Evaluating rules", "percentage": 40 }
}
```

### Session events

The server sends these to every attached client, including the one whose
request caused them. Events name what changed but never carry values: fetch
values with `object.get` at the event's version.

#### `model.changed`

One per committed transaction, whether it touched one object or four hundred.
`version` goes up by exactly one per event; a client that sees a gap calls
`session.snapshot`. `change` is `added`, `removed`, `modified`, `renamed` or
`moved`; `origin.kind` is `apply`, `undo`, `redo` or `reload`.

```json
{
  "jsonrpc": "2.0",
  "method": "model.changed",
  "params": {
    "version": 42,
    "transaction": "t17",
    "origin": { "client": "mcp-1", "kind": "apply" },
    "changes": [
      { "id": "o1k3", "objectKind": "Measure", "change": "renamed", "path": "Sales/Sales Amount", "oldPath": "Sales/Total Sales" },
      { "id": "o1k9", "objectKind": "Measure", "change": "modified", "path": "Sales/Margin %", "properties": ["expression"] }
    ]
  }
}
```

#### `session.state`

`state` is `clean`, `dirty`, `saving`, `stale` or `closed`. A client shows
unsaved changes from it, and offers reload when the session is `stale`.

```json
{
  "jsonrpc": "2.0",
  "method": "session.state",
  "params": { "previous": "clean", "state": "dirty", "version": 43 }
}
```

#### `session.saved`

```json
{
  "jsonrpc": "2.0",
  "method": "session.saved",
  "params": { "version": 43, "savedTo": "/models/sales", "persistence": "file" }
}
```

#### `transaction.opened`

```json
{
  "jsonrpc": "2.0",
  "method": "transaction.opened",
  "params": { "transaction": "t18", "client": "mcp-1", "label": "Margin measures" }
}
```

#### `transaction.closed`

`outcome` is `committed`, `rolledBack` or `timedOut`. A commit is followed by
its `model.changed`.

```json
{
  "jsonrpc": "2.0",
  "method": "transaction.closed",
  "params": { "transaction": "t18", "client": "mcp-1", "outcome": "committed" }
}
```

#### `diagnostics.updated`

The host recomputed dependencies, DAX diagnostics and BPA for `version`, 250 ms
after the last change. Fetch them with `dax.check`, `bpa.run` or `deps.get`.

```json
{ "jsonrpc": "2.0", "method": "diagnostics.updated", "params": { "version": 43 } }
```
