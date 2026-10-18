# ADR 0005: Validate a deploy before it commits

- **Status:** Proposed
- **Date:** 2026-10-18
- **Builds on:** [#237](https://github.com/bgarcevic/tomix/issues/237), the post-deploy check (`TomDeployValidator`)

## Context

#237 made `tx deploy` read back the deployed model and fail (exit 1) when the server reports
an object in an error state (`SemanticError`, `EvaluationError`, `DependencyError`). The server
accepts TMSL whose DAX does not resolve, so this check is where a broken measure or RLS filter
surfaces. But it runs after the deploy has committed: by the time `tx deploy` fails, the broken
model is live on the target.

The question was whether a deploy can run like a transaction: begin, deploy, check, then
commit or roll back. Two options were on the table:

- **A. An explicit XMLA transaction** on the deploy's own session (`Server.BeginTransaction`,
  deploy, read object states, `CommitTransaction` / `RollbackTransaction`).
- **B. A scratch database:** deploy to `<db>__validate`, check it, drop it, then deploy to the
  real target.

### Evidence

`spikes/deploy-transaction` is a throwaway harness that ran against a Power BI Premium
workspace (`powerbi://…/sandbox bkg`) with `samples/deploy-qa` and a copy whose
`Sales[Total Amount]` is `SUM ( Sales[Nope] )`. `spikes/deploy-transaction/run-sandbox.md`
records one full run. In summary:

| # | Probe | Result |
|---|---|---|
| A0 | `BeginTransaction`, `RollbackTransaction`, `CommitTransaction` on an idle session | All accepted by the Power BI endpoint |
| A1 | New database: begin, `createOrReplace` (broken), read states, roll back | States read mid-transaction show the uncommitted `SemanticError` (AMO and the `TMSCHEMA_MEASURES` DMV agree). **Rollback leaves the database in place, broken.** |
| A2/A4 | Existing database: begin, `createOrReplace` (broken), roll back (with and without reads in between) | **Rollback leaves the broken definition in place.** |
| A3 | Existing database: begin, `createOrReplace`, commit | Commit fails: "no active transaction in this session". The database-level `createOrReplace` committed on its own and ended the transaction. |
| A5 | Existing database: begin, change one measure through TOM `SaveChanges`, read states, roll back | Mid-transaction read shows `SemanticError`; **rollback restores the good measure** |
| A6 | Existing, processed database: begin, `Model.CopyTo` of the merged broken model + `SaveChanges`, read states, roll back | Mid-transaction read shows `SemanticError`; **rollback restores the definition, and the processed rows are intact** |
| A7 | As A6 with a valid change, then commit | Commit succeeds; the change is live and the processed rows are intact |

The same flow then ran through the real `tx deploy --rollback-on-error` against that workspace:
a broken model to a new database (created, checked, deleted, exit 1), a valid model to a new
database (created, exit 0), a broken model to the existing database (rolled back, exit 1, the
measure still `SUM ( Sales[Amount] )` afterwards), and a valid model to the existing, refreshed
database (committed, exit 0, row counts unchanged).

Two side findings:

- An ADOMD connection that joins the AMO session by `SessionID` must be closed with
  `Close(endSession: false)`. A plain `Close`/`Dispose` ends the shared session.
- Right after a deploy, a *new* connection to the Power BI endpoint can briefly miss the
  database in `Server.Databases`. The deploy's own connection sees it, which is what the
  validator uses.

## Decision

### 1. Option A, applied as object-level changes

The feasibility answer is: **explicit transactions work on the Power BI XMLA endpoint, but a
database-level `createOrReplace` commits on its own and ends the open transaction.** Option A
therefore works only if the deploy is expressed as changes below the database. `tx deploy
--rollback-on-error` does this for an existing target:

1. Read the target (`Database.Refresh(true)`).
2. Build the merged model with the same `TmslDeployScriptBuilder.BuildDatabaseNode` merge the
   `createOrReplace` script uses (same preservation options, restricted information included so
   preserved connection strings survive), and deserialize it into a detached `Database`.
3. `BeginTransaction`, `merged.Model.CopyTo(target.Model)`, `target.Model.SaveChanges()`. TOM
   sends only the differences, so unchanged tables and partitions keep their data, as an
   ordinary deploy does.
4. `target.Refresh(true)` and `TomDeployValidator.Inspect`: inside the transaction the server
   reports the uncommitted objects' states.
5. Any error, or a check that could not run, means `RollbackTransaction`. Otherwise
   `CommitTransaction`. Any exception before that decision also rolls back.

`Model.CopyTo` onto a server-bound model is already how XMLA live sessions restore a checkpoint
([ADR 0003](adr-0003-live-session-checkpoint-rollback.md) §1).

### 2. A new database is created, checked, and deleted on error

A database cannot be created inside a transaction (A1). The target held no such database
before the deploy, so deleting it restores the target exactly. The flow is the ordinary
`createOrReplace`, then #237's check, then `delete` when the check fails. For a short window the
broken database exists on the target; nothing that already used the target can depend on it.

### 3. Opt-in flag, not the default

`--rollback-on-error` is opt-in. The transactional path sends `alter`/`create`/`delete`
commands generated by TOM instead of the `createOrReplace` script, which is a different code
path on the server for every deploy. It also cannot change the compatibility level (a
database-level change, which commits on its own), so it refuses with a message to deploy once
without the flag. Making it the default is a follow-up once it has run in real pipelines.

The flag cannot be combined with `--xmla`, because a script is never deployed.

### 4. Result contract

- A rolled-back deploy returns `status: "rolled-back"` with the `validation` that caused it and
  fails with `TOMIX_DEPLOY_ROLLED_BACK` (exit 1). The message says the target is unchanged.
- A committed deploy under the flag looks exactly like an ordinary deploy, including #237's
  warnings for incomplete objects.
- Without the flag nothing changes: #237's behaviour stands (`TOMIX_DEPLOY_VALIDATION_FAILED`,
  model deployed).

## Option B (not chosen)

A scratch database `<db>__validate` gives the same answer for a broken model without
transactions: deploy the merged model there, check it, drop it, then deploy for real. It was
kept as the fallback in case option A failed. Compared with option A it:

- deploys twice, and the second deploy is not the one that was checked, so a concurrent change
  to the target between the two can still land a broken model;
- needs create permission in the workspace and leaves a stray dataset if the process dies
  between deploy and drop, and the service list lags (see Evidence), so cleanup must retry;
- cannot use the target's processed data, so it reports `NoData` where the real target would
  not, though that does not affect the error check.

Option A checks the exact change that is committed, on the target itself, in one round trip.

## Consequences

- A CI pipeline can pass `--rollback-on-error` and rely on the target never holding a model that
  `tx deploy` reported as broken.
- An existing target is changed through TOM's diff rather than `createOrReplace`. Differences
  between the two paths (for example, role member IDs that cloud endpoints assign) are covered
  by the same merge, but have so far only been exercised on `samples/deploy-qa`. Broader
  coverage (calculation groups, perspectives, cultures, models with role members) is a
  follow-up before considering the flag as a default.
- `spikes/deploy-transaction` stays on the branch as evidence and can be deleted once this ADR
  is accepted.
