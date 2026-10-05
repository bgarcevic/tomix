# Error Codes Reference

This document lists every diagnostic code emitted by `tx`, grouped by prefix.
Codes are part of the public API surface — renaming or removing a code requires
a major version bump (see [cli-ux-guidelines.md](cli-ux-guidelines.md#versioning-policy)).

## JSON Error Envelope

Under `--output-format json` (or `--error-format json`), errors are emitted on **stderr**
as a single JSON object using the first Error/Fatal diagnostic:

```json
{
  "error": "Human-readable message describing what happened.",
  "code": "TOMIX_MUTATION_FAILED",
  "severity": "Error",
  "hint": "Optional guidance on how to fix the problem."
}
```

Fields:

| Field      | Type   | Description                                              |
|------------|--------|----------------------------------------------------------|
| `error`    | string | The diagnostic message. Always present.                  |
| `code`     | string | The diagnostic code (see tables below). Always present.  |
| `severity` | string | `Info`, `Warning`, `Error`, or `Fatal`.                  |
| `hint`     | string | Optional fix suggestion. May be `null`.                  |
| `blocked` | boolean | Present as `true` when a mutation save is blocked by validation. |
| `reason` | string | `validation` for a blocked mutation save. |
| `newValidationErrorCount` | integer | Count of errors introduced by the mutation. |
| `newErrors` | array | Introduced errors, each with `code`, `message`, and `object`. |
| `objectPath` | string | The model object the error is about, when there is one (for example `tx format --path`). |
| `line`, `column` | integer | 1-based position of the first syntax error, relative to the expression (not the file). |
| `syntaxErrors` | array | Lexer/parser errors in an expression. Each has `stage` (`lex` or `parse`), `code` (the engine's error name, such as `expectedTokenKind` for M or `missingOperand` for DAX), `message`, `line`, `column`, and `endLine`/`endColumn` (the offending token's last character, inclusive). Fields the engine does not know are omitted: lex errors, and parse errors at the end of the input, have no end position. |

## Exit Codes

| Code | Meaning |
|------|---------|
| 0    | Success. |
| 1    | General failure (most errors). |
| 2    | Usage/argument error, IO failure, or pre-condition violation. |
| 3    | Preview only: `deploy` or a partition-risky `refresh` ran without `--yes` where it could not prompt, so it showed the preview and applied nothing. Pass `--yes` to apply. |

Handlers override the default via `TomixResult.Fail(..., exitCode: 2)`. If no exit code
is specified, the default is `1`. Command-line parse errors (unknown option, missing
required argument, invalid option value) also exit `2`.

## Naming Convention

All diagnostic codes use uppercase `SNAKE_CASE` prefixed with `TOMIX_`. For example:
`TOMIX_MUTATION_SAVE_FAILED`, `TOMIX_OBJECT_NOT_FOUND`.

## Mutation Codes (`TOMIX_MUTATION_*`)

Emitted by `MutationRunner` and handlers that participate in the mutation lifecycle
(`add`, `rm`, `set`, `mv`, `replace`, `format`, `bpa run --fix`,
`bpa rules ignore/unignore`).

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_MUTATION_UNSUPPORTED_PROVIDER` | 1 | The model provider does not implement `IModelMutationSession`. |
| `TOMIX_MUTATION_UNSUPPORTED` | 1 | A `NotSupportedException` was thrown during mutation. |
| `TOMIX_ADD_OPTION_UNSUPPORTED` | 1 | An `add` option was supplied for an object type that cannot consume it (e.g. `--columns` on a CalcGroup). |
| `TOMIX_RENAME_BREAKS_REFS` | 1 | `--strict-refs` was set and the rename (`set --set name=...`, `mv`) would leave DAX expressions referencing the old name. By default renames rewrite referencing DAX automatically, so this only fires for references that cannot be rewritten (role RLS filters, or a bare `'Name'` when a table and a calendar share the renamed name) — or, under `--no-fix-refs`, for any reference. Without `--strict-refs` the rename proceeds with a warning listing the objects left broken. |
| `TOMIX_RM_BREAKS_REFS` | 1 | The object being removed (`rm`) is still referenced by DAX expressions. Unlike a rename there is nothing to rewrite, so the removal is blocked and the message lists the referencing objects; `--force` removes anyway and reports the now-broken references. Structural references (relationships, sort-by, hierarchy levels, perspective and translation entries, role permissions) never block — they are cascade-removed with the object. |
| `TOMIX_MUTATION_INVALID_VALUE` | 1 | An `ArgumentException` was thrown — invalid argument value. |
| `TOMIX_MUTATION_FAILED` | 1 | An `InvalidOperationException` was thrown — generic mutation failure. |
| `TOMIX_MUTATION_SAVE_FAILED` | 2 | An `IOException` occurred while saving the model. |

## Object Lookup Codes (`TOMIX_OBJECT_*`)

Emitted by `get`, `deps`, and `format --path` when a model object path fails to resolve.

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_OBJECT_NOT_FOUND` | 1 | The object path matched zero objects. Includes a hint. |
| `TOMIX_OBJECT_AMBIGUOUS` | 1 | The object path matched more than one object. |
| `TOMIX_PROPERTY_NOT_FOUND` | 1 | `get --query` named a property the object does not have, or no object in a `get --where` scope has the filtered property. The hint suggests the closest property, or lists the valid ones when nothing is close. An `annotation:` or `translation:` token that is simply unset is not an error; it reads back empty. |

## BPA Codes (`TOMIX_BPA_*`)

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_BPA_INVALID_FAIL_ON` | 2 | Invalid `--fail-on` or `--bpa-fail-on` value (expected: error, warning). |
| `TOMIX_BPA_RULE_EXISTS` | 2 | `bpa rules add` was given an ID the rules file (or the model's `BestPracticeAnalyzer` annotation) already has. Change it with `bpa rules set`, or remove it first. |
| `TOMIX_BPA_RULE_FIELD_REQUIRED` | 2 | `bpa rules add` is missing `--name`, `--scope`, or `--expression` (the message names which); `bpa rules set` was given no field to change; or a name or expression was set to an empty value. |
| `TOMIX_BPA_RULE_ID_REQUIRED` | 2 | A `bpa rules` subcommand that edits a rule was called without a rule ID. |
| `TOMIX_BPA_RULE_INVALID_SCOPE` | 2 | `bpa rules add/set --scope` named an unknown object type or no type at all. The hint lists the valid scopes. |
| `TOMIX_BPA_RULE_INVALID_SEVERITY` | 2 | `bpa rules add/set --severity` was not `error`, `warning`, `info`, `3`, `2`, or `1`. |
| `TOMIX_BPA_RULE_NOT_FOUND` | 2 | `bpa rules show` or `ignore` was given an ID that no loaded rule has. The hint names up to three IDs that contain the input; `ignore` accepts `--allow-unknown` to use the ID anyway. `bpa rules set` and `remove` fail with it when the edited rules file or model annotation has no rule with that ID; for a built-in rule, the hint points to `bpa rules ignore --user`. |
| `TOMIX_BPA_RULE_NOT_IN_RULESET` | 0 | Warning, not a failure. `bpa rules unignore --user` stopped ignoring the rule for you, but it isn't in the `standard` ruleset, so `bpa run` checks it only with the `--ruleset` the message names (`full`, or a default-off category preset). Unignoring doesn't add a rule to the ruleset. |
| `TOMIX_BPA_RULE_STILL_IGNORED_BY_MODEL` | 0 | Warning, not a failure. `bpa rules unignore --user` stopped ignoring the rule for you, but the active model still ignores it, so `bpa run` still skips it for that model. Run the `tx bpa rules unignore` command in the hint. |
| `TOMIX_BPA_RULE_STILL_IGNORED_BY_USER` | 0 | Warning, not a failure. `bpa rules unignore` removed the rule from the model's ignore list, but you still ignore it with `--user`, so `bpa run` still skips it on this machine. Run `tx bpa rules unignore <rule-id> --user`. |
| `TOMIX_BPA_RULES_FILE_EXISTS` | 2 | `bpa rules init` found an existing rules file. Pass `--overwrite` to replace it with an empty one. |
| `TOMIX_BPA_RULES_FILE_NOT_FOUND` | 2 | `bpa rules set` or `remove` found no rules file. Create one with `bpa rules init` or `bpa rules add`. |
| `TOMIX_BPA_RULES_FILE_REMOTE` | 2 | `bpa rules add/set/remove/init` was pointed at a remote `--rules-file` URL, which can't be edited. Download it and pass the local path. |
| `TOMIX_BPA_RULES_LOAD_FAILED` | 2 | Failed to load the BPA rules catalog, or a rules file or model `BestPracticeAnalyzer` annotation being edited is not a JSON array of rules. |
| `TOMIX_BPA_RULES_TARGET_CONFLICT` | 2 | `bpa rules add/set/remove` was given both a model and `--rules-file`. A model edits its `BestPracticeAnalyzer` annotation; `--rules-file` edits that file. Pass one. |
| `TOMIX_BPA_VERTIPAQ_STATS_MISSING` | 0 | Warning, not a failure. The deploy BPA gate did not check rules that read `Vertipaq_*` statistics, because the model has none. The message names the rules. Collect the statistics with `tx vertipaq --annotate --save` on a deployed model, or on a local model connected to one in workspace mode. |
| `TOMIX_BPA_VIOLATIONS` | 1 | BPA gate blocked the operation: violations at or above the configured threshold were found (with `deploy --fix-bpa`, they remained after auto-fix). Rules that could not be evaluated count as error-severity violations and are named in the message. Use `--skip-bpa` to bypass. |

## Staging Codes (`TOMIX_STAGE_*`)

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_STAGE_NOTHING_TO_COMMIT` | 1 | `stage commit` called with no staged mutations. |
| `TOMIX_STAGE_SOURCE_DRIFT` | 1 | The staged model source has changed since staging. |
| `TOMIX_STAGE_COMMIT_REMOTE_FAILED` | 1 | Failed to deploy staged changes to the remote endpoint. |
| `TOMIX_STAGE_COMMIT_LOCAL_FAILED` | 1 | Failed to apply staged changes locally. |
| `TOMIX_STAGE_PENDING` | 2 | `tx interactive` on a model with staged changes. A session does not pick them up; run `tx stage commit` or `tx stage discard` first. |
| `TOMIX_STAGE_MATERIALIZE_FAILED` | 1 | Failed to materialize the working copy. |
| `TOMIX_STAGE_OPTIONS_CONFLICT` | 2 | Conflicting stage options (`--revert` combined with `--save`, `--save-to`, or `--stage`). |
| `TOMIX_STAGE_SAVE_CONFLICT` | 2 | Conflicting save options (e.g. `--save` and `--stage` together). |
| `TOMIX_STAGE_NOTHING_STAGED` | 1 | `--revert` called with no staged mutation for the model. |
| `TOMIX_STAGE_MANIFEST_CORRUPT` | 2 | A staged manifest exists but no longer parses (torn write, manual edit). Run `tx stage discard` to reset staging for the model. |

## Live Session Codes (`TOMIX_SESSION_*`)

Raised when a command runs against an open live session (`tx shell` and the other session front ends) instead of opening the model itself.

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_SESSION_STAGE_UNSUPPORTED` | 2 | `--stage` or `--revert` on a live session. Its edits already stay in the session until it saves. |
| `TOMIX_SESSION_MODEL_MISMATCH` | 2 | The command names a different model from the one the live session holds. |
| `TOMIX_SESSION_DIRTY` | 1 | `tx interactive` reached `exit` or the end of its input with unsaved changes or an open transaction, and could not ask. Run `save`, or pass `--discard-on-exit` or `--yes`. From `tx serve`: `session.open` or `session.close` would discard unsaved changes; save first or pass `"discard": true`. |
| `TOMIX_SESSION_NO_MODEL` | 2 | `tx interactive` has no model open for the command. Run `connect <path>` first. From `tx serve`: send `session.open` first. |
| `TOMIX_SESSION_COMMAND_UNAVAILABLE` | 2 | The command does not run inside an interactive session. Run it outside. |
| `TOMIX_SESSION_SOURCE_UNSUPPORTED` | 2 | `tx interactive` cannot open this model yet; it opens TMDL folders and `.bim` files. |
| `TOMIX_SESSION_NOTHING_TO_UNDO` | 1 | `undo` with no change to revert. |
| `TOMIX_SESSION_NOTHING_TO_REDO` | 1 | `redo` with no undone change to reapply. |
| `TOMIX_SESSION_IN_TRANSACTION` | 2 | `undo` or `redo` while a transaction is open. Run `commit` or `rollback` first. |
| `TOMIX_SESSION_TRANSACTION_OPEN` | 2 | `begin` while a transaction is open; transactions do not nest. |
| `TOMIX_SESSION_NO_TRANSACTION` | 2 | `commit` or `rollback` with no open transaction. |
| `TOMIX_SESSION_TRANSACTION_ENDED` | 1 | The transaction was rolled back because it sat idle for 15 minutes. |
| `TOMIX_SESSION_IN_USE` | 1 | A session client sent `session.open` or `session.close` while other clients are connected to the same session. Disconnect them first, or start another session for the other model. |
| `TOMIX_PROTOCOL_VERSION` | 2 | A `tx serve` client asked for a session protocol version the server does not speak. `data.supported` lists the versions it does ([session protocol](protocol.md)). |

## Local Endpoint Codes (`TOMIX_UI_*`)

Answered by the localhost endpoint of a shared session ([session protocol](protocol.md#websocket)) as an HTTP status with a JSON body `{"code": "...", "error": "..."}`. They have no exit code.

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_UI_UNAUTHORIZED` | — | HTTP 401: the request carries no session token, or the wrong one. Read it from the session's file in `~/.tomix/live/`. |
| `TOMIX_UI_FORBIDDEN` | — | HTTP 403: the `Host` header names another server, or `Origin` is another web origin. Only the session's own page and non-browser tools on this machine may connect. |
| `TOMIX_UI_NOT_WEBSOCKET` | — | HTTP 400: a plain HTTP request to `/ws`, which takes WebSocket upgrades only. |
| `TOMIX_UI_NOT_FOUND` | — | HTTP 404: nothing is served at that path. |

## Save Codes (`TOMIX_SAVE_*`)

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_SAVE_OUTPUT_REQUIRED` | 2 | `save` called without an output path and none could be inferred. |
| `TOMIX_SAVE_OUTPUT_EXISTS` | 2 | The output path already exists (use `--overwrite` to replace it). |
| `TOMIX_SAVE_FIX_UNSUPPORTED` | 2 | `save --fix` is not supported by the provider. |
| `TOMIX_SAVE_UNSUPPORTED_PROVIDER` | 1 | The provider does not support model export/saving. |
| `TOMIX_SAVE_UNSUPPORTED_SERIALIZATION` | 2 | The requested serialization format is not supported. |
| `TOMIX_SAVE_VALIDATION_BLOCKED` | 1 | A mutation or staged commit introduced validation errors; no target was written. Use `--force` to save anyway. |
| `TOMIX_SAVE_VALIDATION_FORCED` | 0 | Non-fatal notice on a forced save that introduced validation errors. |

## Deploy Codes (`TOMIX_DEPLOY_*`)

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_DEPLOY_NO_TARGET` | 2 | `deploy` called without a target server, or without `-d/--database` for a model that has no name of its own (such as a bare TMDL folder). |
| `TOMIX_DEPLOY_PROFILE_NO_SERVER` | 2 | `deploy --profile` selected an existing profile without a server; deploy requires a remote profile. |
| `TOMIX_DEPLOY_UNSUPPORTED` | 2 | The source model cannot be deployed (wrong provider/type). |
| `TOMIX_DEPLOY_FIX_UNSUPPORTED` | 2 | `deploy --fix-bpa` was requested but the provider session does not implement `IModelMutationSession`. |
| `TOMIX_DEPLOY_INVALID_FLAGS` | 2 | Contradictory deployment scope: `--deploy-full` combined with a granular `--deploy-*` flag, or a dependent flag without the one it refines (`--deploy-policy-partitions` without `--deploy-partitions`, `--deploy-role-members` without `--deploy-roles`). |
| `TOMIX_DEPLOY_FAILED` | 1 | The deployment operation failed. |

## Refresh Codes (`TOMIX_REFRESH_*`)

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_REFRESH_NO_REMOTE_TARGET` | 2 | `refresh` could not resolve a remote endpoint (default connection is local and no remote workspace-mode secondary is set). |
| `TOMIX_REFRESH_UNSUPPORTED` | 2 | The provider session does not implement `IModelRefreshSession` (e.g. a local TMDL/BIM model). |
| `TOMIX_REFRESH_BAD_TYPE` | 2 | `--type` was not one of `full`, `dataonly`, `automatic`, `calculate`, `clearvalues`, `defragment`, `add`. |
| `TOMIX_REFRESH_TABLE_PARTITION_CONFLICT` | 2 | `--table` and `--partition` were passed together; choose one. |
| `TOMIX_REFRESH_BAD_PARTITION` | 2 | A `--partition` value was not formatted as `TableName.PartitionName`. |
| `TOMIX_REFRESH_FAILED` | 1 | The refresh command was rejected by the server (table not found, no permissions, etc.). |

## Query Codes (`TOMIX_QUERY_*`)

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_QUERY_REQUIRED` | 2 | `query` called without query text: pass it positionally, via `--query`, `--file`, or piped stdin. |
| `TOMIX_QUERY_INPUT_CONFLICT` | 2 | More than one query source was passed: the positional text, `--query`, and `--file` are mutually exclusive; choose one. |
| `TOMIX_QUERY_FILE_NOT_FOUND` | 2 | The `--file` path does not exist. |
| `TOMIX_QUERY_BAD_PARAM` | 2 | A `--param` value was not formatted as `name=value`. |
| `TOMIX_QUERY_OUTPUT_FORMAT` | 2 | `-o`/`--output-file` could not resolve a json or csv format (pass `--output-format json\|csv` or use a `.json`/`.csv` extension). |
| `TOMIX_QUERY_INVALID` | 2 | The query does not start with `EVALUATE`, `DEFINE`, or `SELECT`; bypass with `--no-validate`. |
| `TOMIX_QUERY_NO_REMOTE_TARGET` | 2 | `query` could not resolve a live endpoint (default connection is local and no remote workspace-mode secondary is set). |
| `TOMIX_QUERY_UNSUPPORTED` | 2 | The provider session does not implement `IModelQuerySession` (e.g. a local TMDL/BIM model). |
| `TOMIX_QUERY_FAILED` | 1 | The query was rejected or failed on the server (DAX error, no permissions, timeout, etc.). |

The performance options are **best-effort** and do not have dedicated error codes: `--trace` and
`--cold` require admin rights on the endpoint (and are unavailable on shared-capacity Power BI).
When they cannot be honored, `tx query` prints a one-line warning to stderr, still returns the
rowset, and exits `0`. When `--trace` was requested, a `null` `timings` field is always accompanied
by such a warning.

## Regression Test Codes (`TOMIX_TEST_*`)

Emitted by `test` (DAX regression tests against a live model).

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_TEST_PATH_NOT_FOUND` | 2 | The path argument does not exist. |
| `TOMIX_TEST_NONE_FOUND` | 2 | No `.dax` test files were found under the path (or none match `--filter`). |
| `TOMIX_TEST_BAD_PARAM` | 2 | A `--param` value was not formatted as `name=value`. |
| `TOMIX_TEST_NO_REMOTE_TARGET` | 2 | `test` could not resolve a live endpoint (default connection is local and no remote workspace-mode secondary is set). |
| `TOMIX_TEST_UNSUPPORTED` | 2 | The provider session does not support queries (e.g. a local TMDL/BIM model). |
| `TOMIX_TEST_UPDATE_FAILED` | 1 | An `.expected.json` snapshot could not be written during `--update`. |

Test failures themselves carry **no** diagnostic code: a run whose tests fail (or are missing
their snapshot) renders the full report and exits `1`, mirroring `bpa run`. `test` also reuses
`TOMIX_NO_PROVIDER` and `TOMIX_AUTH_REQUIRED`.

## Incremental Refresh Codes (`TOMIX_REFRESH_POLICY_*`)

Emitted by policy mutations (`set`/`rm`) and `refresh --policy-only`.

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_REFRESH_POLICY_OPTIONS_CONFLICT` | 2 | Invalid policy-only scope, conflicting refresh flags, or nonpositive parallelism. |
| `TOMIX_REFRESH_POLICY_NOT_FOUND` | 1 | `rm`/`refresh --policy-only` targeted a table that has no incremental refresh policy. |
| `TOMIX_REFRESH_POLICY_INVALID` | 1 | `set` produced validation errors (missing range parameters, source expression not referencing RangeStart/RangeEnd, incoherent granularity/periods, incompatible compatibility level) and `--force` was not passed. |
| `TOMIX_REFRESH_POLICY_UNSUPPORTED` | 2 | `apply` targeted a session that is not XMLA-backed (partition generation runs on the server). |
| `TOMIX_REFRESH_POLICY_APPLY_FAILED` | 1 | The server rejected the `apply` operation. |

Policy commands also reuse `TOMIX_OBJECT_NOT_FOUND` (table missing), `TOMIX_REFRESH_NO_REMOTE_TARGET` (`refresh --policy-only` with no remote endpoint), `TOMIX_NO_PROVIDER`, `TOMIX_AUTH_REQUIRED`, and the `TOMIX_MUTATION_*` / `TOMIX_STAGE_*` families via the shared mutation runner. Validation findings surfaced in the result payload (e.g. `range_parameter_missing`, `granularity_order`, `no_polling_expression`) are lowercase snake tokens, not `TOMIX_` diagnostic codes.

## VertiPaq Codes (`TOMIX_VERTIPAQ_*` / `TOMIX_VPAX_*`)

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_VERTIPAQ_UNSUPPORTED_SOURCE` | 2 | The source is a local model definition (TMDL/BIM) with no live storage engine; connect to a deployed model or use `--import`. |
| `TOMIX_VERTIPAQ_OPTIONS_CONFLICT` | 2 | Conflicting options: `--import` with `--export` or `--annotate`, `--obfuscate` without `--export`, `--save` without `--annotate`, or csv output with multiple views. |
| `TOMIX_VERTIPAQ_INVALID_FIELDS` | 2 | `--fields` was combined with multiple views, or contained an unknown field token for the selected view. |
| `TOMIX_VERTIPAQ_INVALID_TOP` | 2 | `--top` was zero or negative; it must be a positive integer. |
| `TOMIX_VERTIPAQ_TABLE_NOT_FOUND` | 1 | The positional table filter matched no table in the statistics. |
| `TOMIX_VERTIPAQ_FAILED` | 1 | Statistics extraction against the live engine failed. |
| `TOMIX_VPAX_READ_FAILED` | 2 | The `--import` file is missing, unreadable, or not a valid statistics package. |
| `TOMIX_VPAX_WRITE_FAILED` | 2 | The `--export` target (or its obfuscation dictionary) could not be written. |

## Init Codes (`TOMIX_INIT_*`)

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_INIT_OUTPUT_REQUIRED` | 2 | `init` called without an output path. |
| `TOMIX_INIT_OUTPUT_EXISTS` | 2 | The output path already exists. |
| `TOMIX_INIT_UNSUPPORTED_SERIALIZATION` | 2 | The requested serialization format is not supported for `init`. |
| `TOMIX_INIT_UNSUPPORTED_COMPATIBILITY_MODE` | 2 | The requested compatibility level is not supported. |

## Auth Codes (`TOMIX_AUTH_*`)

> **Note:** Codes in this section are diagnostic codes, not environment variables.
> See [Environment Variables](#environment-variables) for auth-related env vars.

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_AUTH_FAILED` | 1 | Authentication failed (invalid credentials, token expired, etc.). |
| `TOMIX_AUTH_REQUIRED` | 1 | Authentication is required but no credentials were provided. |
| `TOMIX_AUTH_SECRET_REQUIRED` | 2 | A service-principal login needs a secret and none was provided: no source given, empty stdin under `--password -`, an empty secret file, or an empty interactive prompt. |
| `TOMIX_AUTH_SECRET_SOURCE_CONFLICT` | 2 | `--password -` and `--password-file` (or the certificate-password equivalents) were combined; choose one. |
| `TOMIX_AUTH_SECRET_FILE_NOT_FOUND` | 2 | The `--password-file` / `--certificate-password-file` path does not exist. |

## Config Codes (`TOMIX_CONFIG_*`)

> **Note:** `TOMIX_CONFIG_DIR` is an environment variable, not a diagnostic code. See below.

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_CONFIG_INVALID_VALUE` | 2 | `config set` called with an invalid value. |
| `TOMIX_CONFIG_UNKNOWN_KEY` | 2 | `config set` called with an unknown configuration key. |

## Profile Codes

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_PROFILE_NOT_FOUND` | 1 | The named profile was not found. |
| `TOMIX_PROFILE_NAME_REQUIRED` | 2 | `profile set` called without a profile name. |
| `TOMIX_NO_ACTIVE_CONNECTION` | 2 | `profile set --from-active` called with no active connection. |

## Validate Issue Codes

Codes carried on the issues inside a `validate` result (not top-level diagnostics; the
command exits `1` when any error-severity issue is present). `DAX0001`–`DAX0003` and
`DAX0006` come from the offline DAX reference scan, `DAX0004`/`DAX0005` from the offline DAX syntax check's
token pass, and `DAX0007`–`DAX0009` from its grammar pass (the first grammar error only, and
only when the tokens are sound). The syntax check runs first — a broken expression reports
only its syntax issues; `TOMIX_*` codes
come from structural integrity checks.

| Code | Severity | Trigger |
|------|----------|---------|
| `DAX0001` | Error | A DAX expression references a table that does not exist in the model. |
| `DAX0002` | Error | A DAX expression references a column that does not exist on the named table (and no measure by that name exists). |
| `DAX0003` | Warning | An unqualified `[X]` reference resolves to no measure or column anywhere in the model, or is a column the expression builds itself (`ADDCOLUMNS`, `SUMMARIZE`, `GENERATESERIES`, ...) used outside the table that has it. Columns the expression builds are not reported where a row of their table is in context (inside `FILTER`, `SUMX`, and other iterators over it, including through a `VAR`). Warning-severity because offline analysis cannot be certain. |
| `DAX0004` | Error | A DAX expression contains a character that starts no DAX token, or an unbalanced parenthesis/brace. The expression's reference checks are skipped. |
| `DAX0005` | Error | A DAX expression contains an unterminated string, table name, column reference, or block comment. The expression's reference checks are skipped. |
| `DAX0006` | Error | A measure or calculated column expression directly references itself. |
| `DAX0007` | Error | The DAX parser found a token where a comma, a closing bracket, or the end of the expression belongs: a missing comma between arguments (`SUM(a b)`), or text left over after a complete expression. The expression's reference checks are skipped. |
| `DAX0008` | Error | The DAX parser found an operator, a closing bracket, or the end of the expression where an operand belongs (`1 +`, `1 * * 2`, `f(x + )`). The expression's reference checks are skipped. |
| `DAX0009` | Error | A `VAR` has no `=` after its name, or a `VAR` block has no `RETURN`. The expression's reference checks are skipped. |
| `TOMIX_BROKEN_RELATIONSHIP` | Error | A relationship endpoint refers to a missing column. |
| `TOMIX_BROKEN_SORT_BY` | Error | A column's sort-by column does not exist on its table. |
| `TOMIX_BROKEN_LEVEL` | Error | A hierarchy level is bound to a column that does not exist on its table. |
| `TOMIX_MODEL_LOAD_FAILED` | Error | The model could not be opened or snapshotted at all (e.g. TMDL with unresolvable references); the provider's message is passed through. |

## General Codes

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_NO_PROVIDER` | 2 | No registered provider can open the model. A model source that exists on disk but cannot be read reports `TOMIX_MODEL_LOAD_FAILED` instead, naming the unreadable path. |
| `TOMIX_PROVIDER_AMBIGUOUS` | 1 | More than one registered provider claims the model reference. Provider `CanOpen` contracts are mutually exclusive, so this indicates a provider-registration bug, not a problem with the model. |
| `TOMIX_MODEL_LOAD_FAILED` | 2 | The model source could not be loaded: a provider matched but loading failed (unparsable TMDL/BIM, unresolvable references), or the source exists on disk but cannot be read at all (permissions), in which case no provider matched and the message names the unreadable path. The command never ran. |
| `TOMIX_NO_MODEL` | 2 | No model reference was provided and none could be inferred. |
| `TOMIX_CONNECT_FAILED` | 1 | `connect` failed to establish a session. |
| `TOMIX_INTERACTIVE_REQUIRED` | 1 | An interactive-only flow (`connect --remote`, a valueless `-w`) was invoked without a TTY (e.g. `--non-interactive`, `--quiet`, redirected input, or json/csv output). Pass the workspace/model explicitly. |
| `TOMIX_REMOTE_LIST_FAILED` | 1 | Listing workspaces or models failed (Power BI REST or XMLA error) during an interactive `connect` or `connect <server> --list`. |
| `TOMIX_DATABASE_NOT_FOUND` | 1 | The database/model name was not found on the server. |
| `TOMIX_DATABASE_REQUIRED` | 2 | The endpoint hosts more than one database/model and none was named. List them with `tx connect <server> --list`, then pass one with `-d/--database`. |
| `TOMIX_DEPS_PATH_REQUIRED` | 2 | `get --deps` (or `deps`) called without an object path. |
| `TOMIX_SINGLE_OBJECT_REQUIRED` | 2 | `get --query` or `get --deps` was given a path that selects a set (a wildcard such as `Sa*`, a container such as `Sales/Measures`, or `--ls`/`--where`). Name one object. |
| `TOMIX_UNUSED_PATH` | 2 | `get --unused` (or `deps --unused`) was given a path; it scans the whole model. |
| `TOMIX_INVALID_WHERE` | 2 | A `get --where` value is not `Prop=Value`. |
| `TOMIX_FIND_INVALID_REGEX` | 2 | `find --regex` called with an invalid regular expression pattern. |
| `TOMIX_UNKNOWN_OPTION` | 2 | An unrecognized `--option` would have been bound to a positional argument (e.g. a typo'd flag). Put `--` before positional values that must start with `-`. |
| `TOMIX_UNKNOWN_COMMAND` | 2 | The subcommand name is not one `tx` (or the named command group) has, e.g. `tx lss`. The hint suggests the closest command. A command group run with no subcommand (e.g. `tx bpa`) is not an error: it prints the group's help and exits 0. |
| `TOMIX_USAGE` | 2 | The command line does not parse: a required argument or option value is missing, a value is not one the option accepts, or there is an extra argument. The hint names the command's `--help`. |
| `TOMIX_QUIET_COLLISION` | 2 | Text was passed right after `-q`, but `-q` means `--quiet` and never consumes a value: on `query` the text would run as the positional query with quiet on, and on `get` it was read as the optional `[model]` path. Pass query text positionally or via `--query`. |
| `TOMIX_MOVE_UNSUPPORTED` | 1 | `mv` across tables called with an unsupported path shape; a cross-table move needs a `Table/Measure` source and destination. Moving a non-measure across tables surfaces as `TOMIX_MUTATION_UNSUPPORTED` from the provider. |
| `TOMIX_MOVE_INVALID_PATH` | 2 | `mv` source or destination is missing an object name (empty path, trailing `/`). |
| `TOMIX_MOVE_NOOP` | 1 | `mv` source and destination are identical; nothing to rename. |
| `TOMIX_REPLACE_PATTERN_REQUIRED` | 2 | `replace` called without a search pattern. |
| `TOMIX_SET_PROPERTY_REQUIRED` | 2 | `set` called without a property to set. |
| `TOMIX_FORMAT_UNSUPPORTED_LANGUAGE` | 2 | `format` called with an unsupported expression language. |
| `TOMIX_FORMAT_FAILED` | 1 | `format` could not format the inline expression (`--expression`) or the object (`--path`), or a whole-model run had failed expressions and applied nothing (the result rows are still written; each failed row's `error` says why). DAX and M are both formatted offline. The message includes the formatter's error details; DAX or M that does not lex or parse also carries `line`, `column`, and `syntaxErrors` (plus `objectPath` for `--path`). |
| `TOMIX_COMPLETION_UNSUPPORTED_SHELL` | 2 | `completion` called with an unsupported shell name. |
| `TOMIX_COMPLETION_SHELL_REQUIRED` | 2 | `completion` called without its required shell argument. |
| `TOMIX_PROFILE_TARGET_REQUIRED` | 2 | A new profile has no usable remote, local-model, Desktop, or active-session target. |
| `TOMIX_INVALID_OUTPUT_FORMAT` | 2 | `--output-format` value is not one of: auto, text, json, csv, tmsl, bim, tmdl. |
| `TOMIX_INVALID_TYPE` | 2 | `--type` value is not a known object kind; the hint lists the valid types. |
| `TOMIX_CONFIRMATION_REQUIRED` | 1 | A destructive action needed confirmation but prompting was unavailable (`--non-interactive`, `--quiet`, json/csv output, or redirected stdin/stderr). Pass `--yes`. |
| `TOMIX_OPTION_CONFLICT` | 2 | Mutually-exclusive options were combined (e.g. `--recent` with a model path, `--profile` with an explicit server/database). The message names the pair; which options conflict is command-specific, so branch on the code rather than the wording. |
| `TOMIX_RECENT_INVALID` | 2 | `--recent` was given a value that is not a positive index (`1` = most recently used). |
| `TOMIX_RECENT_NONE` | 1 | `--recent` was used but no connection has been made yet. Connect once with `tx connect <server> <database>`. |
| `TOMIX_RECENT_OUT_OF_RANGE` | 1 | `--recent <n>` is higher than the number of stored connections; the message states how many exist. |
| `TOMIX_RECENT_INDEX_REQUIRED` | 2 | A valueless `--recent` needs its interactive picker, but prompting was unavailable (`--non-interactive`, `--quiet`, json/csv output, or redirected stdin/stderr). Pass `--recent <n>`. |
| `TOMIX_WORKSPACE_PRIMARY_REQUIRED` | 2 | `connect -w` was given without enough of a primary connection to mirror from (needs `<server> <database>`, or a local path). A missing value, not a conflict — see `TOMIX_OPTION_CONFLICT` for that. |
| `TOMIX_WORKSPACE_UNREACHABLE` | 1 | `connect -w` reached the primary model but could not reach the workspace mirror's server. The connection is left unchanged. |
| `TOMIX_OUTPUT_FORMAT_UNSUPPORTED` | 2 | The command cannot render the requested `--output-format`; the message lists the formats it supports. |
| `TOMIX_CONFIG_CORRUPT` | 2 | `~/.tomix/config.json` exists but does not parse. Repair it manually or reset it with `tx config init --overwrite`; help, version, doctor, and config recovery paths remain available. |
| `TOMIX_UNEXPECTED` | 1 | An unexpected exception reached the top-level handler. The stack trace is only printed under `--debug`; with `--error-format json` it is embedded as a `detail` field in the envelope so stderr stays valid JSON. |

## Update Codes (`TOMIX_UPDATE_*`)

Emitted by `tx update` (see `docs/commands/manage.md`). `tx update --check` exits `0`
whether or not an update is available; these codes cover failures only.

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_UPDATE_CHECK_FAILED` | 1 | Could not read the release list from api.github.com (network failure, or no releases published). |
| `TOMIX_UPDATE_VERSION_NOT_FOUND` | 2 | `--version <v>` names a version that is not a published release. |
| `TOMIX_UPDATE_UNSUPPORTED_INSTALL` | 2 | The running tx is not updatable in place (dev wrapper, or unrecognized install layout). |
| `TOMIX_UPDATE_TOOL_FAILED` | 1 | `dotnet tool update -g Tomix.Cli` exited non-zero. |
| `TOMIX_UPDATE_DOWNLOAD_FAILED` | 1 | The release asset or `checksums.txt` could not be downloaded. |
| `TOMIX_UPDATE_CHECKSUM_MISMATCH` | 1 | The downloaded asset's SHA-256 does not match `checksums.txt`; nothing was installed. |
| `TOMIX_UPDATE_APPLY_FAILED` | 1 | The binary swap failed (permissions, IO); the previous binary is restored. |

## Environment Variables

The following `TOMIX_*` tokens are **environment variables**, not diagnostic codes:

| Variable | Description |
|----------|-------------|
| `TOMIX_AUTH_CLIENT_ID` | Azure AD client id for service principal auth. |
| `TOMIX_AUTH_TENANT` | Azure AD tenant id for service principal auth. |
| `TOMIX_SESSION` | Session id for persisting the active model connection. Unset, the session is scoped to the git repository/worktree root or the current folder. |
| `TOMIX_CONFIG_DIR` | Custom path to the configuration directory. |
| `TOMIX_NO_UPDATE_CHECK` | Set (any value) to disable the throttled update check and notice. |

## Migration: Unified Mutation Error Codes

The following error codes were replaced by unified `TOMIX_MUTATION_*` codes.
Scripts that pattern-match on the old codes should update to the new ones.

| Old code | New code |
|----------|----------|
| `TOMIX_REPLACE_INVALID_ARGUMENT` | `TOMIX_MUTATION_INVALID_VALUE` |
| `TOMIX_REPLACE_UNSUPPORTED` | `TOMIX_MUTATION_UNSUPPORTED` |
| `TOMIX_REPLACE_FAILED` | `TOMIX_MUTATION_FAILED` |
| `TOMIX_REPLACE_SAVE_FAILED` | `TOMIX_MUTATION_SAVE_FAILED` |
| `TOMIX_SCRIPT_SAVE_UNSUPPORTED` | `TOMIX_MUTATION_UNSUPPORTED` |
| `TOMIX_SCRIPT_SAVE_FAILED` | `TOMIX_MUTATION_SAVE_FAILED` |
| `TOMIX_BPA_FIX_UNSUPPORTED` | `TOMIX_MUTATION_UNSUPPORTED_PROVIDER` |
| `TOMIX_BPA_IGNORE_UNSUPPORTED` | `TOMIX_MUTATION_UNSUPPORTED_PROVIDER` |

### What did not change

Command-specific validation codes (checked before entering `MutationRunner`) are unchanged:

- `TOMIX_REPLACE_PATTERN_REQUIRED`
- `TOMIX_SET_PROPERTY_REQUIRED`
- `TOMIX_BPA_RULE_ID_REQUIRED`
- `TOMIX_FORMAT_UNSUPPORTED_LANGUAGE`
