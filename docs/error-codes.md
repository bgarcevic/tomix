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

## Exit Codes

| Code | Meaning |
|------|---------|
| 0    | Success. |
| 1    | General failure (most errors). |
| 2    | Usage/argument error, IO failure, or pre-condition violation. |

Handlers override the default via `TomixResult.Fail(..., exitCode: 2)`. If no exit code
is specified, the default is `1`. Command-line parse errors (unknown option, missing
required argument, invalid option value) also exit `2`.

## Naming Convention

All diagnostic codes use uppercase `SNAKE_CASE` prefixed with `TOMIX_`. For example:
`TOMIX_MUTATION_SAVE_FAILED`, `TOMIX_OBJECT_NOT_FOUND`.

## Mutation Codes (`TOMIX_MUTATION_*`)

Emitted by `MutationRunner` and handlers that participate in the mutation lifecycle
(`add`, `rm`, `set`, `mv`, `replace`, `format`, `script`, `bpa run --fix`,
`bpa rules ignore/unignore`).

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_MUTATION_UNSUPPORTED_PROVIDER` | 1 | The model provider does not implement `IModelMutationSession`. |
| `TOMIX_MUTATION_UNSUPPORTED` | 1 | A `NotSupportedException` was thrown during mutation. |
| `TOMIX_ADD_OPTION_UNSUPPORTED` | 1 | An `add` option was supplied for an object type that cannot consume it (e.g. `--columns` on a CalcGroup). |
| `TOMIX_RENAME_BREAKS_REFS` | 1 | `--strict-refs` was set and the rename (`set -q name`, `mv`) would leave DAX expressions referencing the old name. By default renames rewrite referencing DAX automatically, so this only fires for references that cannot be rewritten (role RLS filters) — or, under `--no-fix-refs`, for any reference. Without `--strict-refs` the rename proceeds with a warning listing the objects left broken. |
| `TOMIX_MUTATION_INVALID_VALUE` | 1 | An `ArgumentException` was thrown — invalid argument value. |
| `TOMIX_MUTATION_FAILED` | 1 | An `InvalidOperationException` was thrown — generic mutation failure. |
| `TOMIX_MUTATION_SAVE_FAILED` | 2 | An `IOException` occurred while saving the model. |

## Object Lookup Codes (`TOMIX_OBJECT_*`)

Emitted by `get`, `deps`, and `format -p` when a model object path fails to resolve.

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_OBJECT_NOT_FOUND` | 1 | The object path matched zero objects. Includes a hint. |
| `TOMIX_OBJECT_AMBIGUOUS` | 1 | The object path matched more than one object. |

## BPA Codes (`TOMIX_BPA_*`)

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_BPA_INVALID_FAIL_ON` | 2 | Invalid `--fail-on` value (expected: error, warning). |
| `TOMIX_BPA_RULE_ID_REQUIRED` | 2 | `bpa rules ignore/unignore` called without a rule id. |
| `TOMIX_BPA_RULES_LOAD_FAILED` | 2 | Failed to load the BPA rules catalog. |
| `TOMIX_BPA_VIOLATIONS` | 1 | BPA gate blocked the operation: violations found (or, under `--fix-bpa`, error-severity violations remained after auto-fix). Use `--skip-bpa` to bypass. |

## Staging Codes (`TOMIX_STAGE_*`)

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_STAGE_NOTHING_TO_COMMIT` | 1 | `stage commit` called with no staged mutations. |
| `TOMIX_STAGE_SOURCE_DRIFT` | 1 | The staged model source has changed since staging. |
| `TOMIX_STAGE_COMMIT_REMOTE_FAILED` | 1 | Failed to deploy staged changes to the remote endpoint. |
| `TOMIX_STAGE_COMMIT_LOCAL_FAILED` | 1 | Failed to apply staged changes locally. |
| `TOMIX_STAGE_MATERIALIZE_FAILED` | 1 | Failed to materialize the working copy. |
| `TOMIX_STAGE_OPTIONS_CONFLICT` | 2 | Conflicting stage options (`--revert` combined with `--save`, `--save-to`, or `--stage`). |
| `TOMIX_STAGE_SAVE_CONFLICT` | 2 | Conflicting save options (e.g. `--save` and `--stage` together). |
| `TOMIX_STAGE_NOTHING_STAGED` | 1 | `--revert` called with no staged mutation for the model. |

## Save Codes (`TOMIX_SAVE_*`)

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_SAVE_OUTPUT_REQUIRED` | 2 | `save` called without an output path and none could be inferred. |
| `TOMIX_SAVE_OUTPUT_EXISTS` | 2 | The output path already exists (use `--force` to overwrite). |
| `TOMIX_SAVE_FIX_UNSUPPORTED` | 2 | `save --fix` is not supported by the provider. |
| `TOMIX_SAVE_UNSUPPORTED_PROVIDER` | 1 | The provider does not support model export/saving. |
| `TOMIX_SAVE_UNSUPPORTED_SERIALIZATION` | 2 | The requested serialization format is not supported. |

## Deploy Codes (`TOMIX_DEPLOY_*`)

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_DEPLOY_NO_TARGET` | 2 | `deploy` called without a target server/database. |
| `TOMIX_DEPLOY_UNSUPPORTED` | 2 | The source model cannot be deployed (wrong provider/type). |
| `TOMIX_DEPLOY_FIX_UNSUPPORTED` | 2 | `deploy --fix-bpa` was requested but the provider session does not implement `IModelMutationSession`. |
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

## Script Codes (`TOMIX_SCRIPT_*`)

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_SCRIPT_FILE_NOT_FOUND` | 1 | The script file was not found. |
| `TOMIX_SCRIPT_REQUIRED` | 2 | `script` called without a script file or inline script. |

## Macro Codes (`TOMIX_MACRO_*`)

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_MACRO_FILE_EXISTS` | 2 | A macro file already exists at the target path. |
| `TOMIX_MACRO_FILE_NOT_FOUND` | 1 | The macro file was not found. |
| `TOMIX_MACRO_NOT_FOUND` | 1 | The macro name was not found in the catalog. |
| `TOMIX_MACRO_SCRIPT_NOT_FOUND` | 1 | The macro's script template was not found. |
| `TOMIX_MACRO_UNKNOWN_PROPERTY` | 2 | The macro uses an unknown property. |

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

## General Codes

| Code | Exit | Trigger |
|------|------|---------|
| `TOMIX_NO_PROVIDER` | 2 | No registered provider can open the model. |
| `TOMIX_NO_MODEL` | 2 | No model reference was provided and none could be inferred. |
| `TOMIX_CONNECT_FAILED` | 1 | `connect` failed to establish a session. |
| `TOMIX_CONNECT_INVALID_TARGET` | 1 | `connect` was given a server that is neither a remote endpoint nor a local model path. |
| `TOMIX_DATABASE_NOT_FOUND` | 1 | The database/model name was not found on the server. |
| `TOMIX_DEPS_PATH_REQUIRED` | 2 | `deps` called without an object path. |
| `TOMIX_FIND_INVALID_REGEX` | 2 | `find --regex` called with an invalid regular expression pattern. |
| `TOMIX_UNKNOWN_OPTION` | 2 | An unrecognized `--option` would have been bound to a positional argument (e.g. a typo'd flag). Put `--` before positional values that must start with `-`. |
| `TOMIX_MOVE_UNSUPPORTED` | 1 | `mv` called for an unsupported object type or operation (e.g. moving between parents). |
| `TOMIX_MOVE_INVALID_PATH` | 2 | `mv` source or destination is missing an object name (empty path, trailing `/`). |
| `TOMIX_MOVE_NOOP` | 1 | `mv` source and destination are identical; nothing to rename. |
| `TOMIX_REPLACE_PATTERN_REQUIRED` | 2 | `replace` called without a search pattern. |
| `TOMIX_SET_PROPERTY_REQUIRED` | 2 | `set` called without a property to set. |
| `TOMIX_FORMAT_UNSUPPORTED_LANGUAGE` | 2 | `format` called with an unsupported expression language. |
| `TOMIX_COMPLETION_UNSUPPORTED_SHELL` | 2 | `completion` called with an unsupported shell name. |

## Environment Variables

The following `TOMIX_*` tokens are **environment variables**, not diagnostic codes:

| Variable | Description |
|----------|-------------|
| `TOMIX_AUTH_CLIENT_ID` | Azure AD client id for service principal auth. |
| `TOMIX_AUTH_TENANT` | Azure AD tenant id for service principal auth. |
| `TOMIX_AUTH_CLIENT_SECRET` | Azure AD client secret for service principal auth. |
| `TOMIX_AUTH_CERTIFICATE` | Path to a certificate for service principal auth. |
| `TOMIX_AUTH_CERTIFICATE_PASSWORD` | Password for the auth certificate. |
| `TOMIX_MACROS_PATH` | Custom path to the macro catalog directory. |
| `TOMIX_SESSION` | Session id for persisting the active model connection. |
| `TOMIX_CONFIG_DIR` | Custom path to the configuration directory. |
| `TOMIX_POWERQUERY_FORMATTER_API` | Custom Power Query Formatter API endpoint URL. |

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
- `TOMIX_SCRIPT_FILE_NOT_FOUND`
- `TOMIX_SCRIPT_REQUIRED`
- `TOMIX_BPA_RULE_ID_REQUIRED`
- `TOMIX_FORMAT_UNSUPPORTED_LANGUAGE`
