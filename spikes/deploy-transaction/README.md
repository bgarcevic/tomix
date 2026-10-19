# Deploy transaction spike (throwaway)

Answers one question: can `tx deploy` run the deploy TMSL inside an explicit XMLA
transaction (`BeginTransaction`), read object states with `TomDeployValidator`, and
`RollbackTransaction` so a broken model never reaches the target?

Not part of `Tomix.slnx` and not shipped. Delete this folder once the decision in
`docs/design/adr-0005-deploy-validate-before-commit.md` is settled.

## Run

```powershell
.\tx.ps1 auth login
dotnet run --project spikes/deploy-transaction -- "powerbi://api.powerbi.com/v1.0/myorg/<Workspace>"
```

It reuses the token cache `tx auth login` writes; no credentials are passed or stored.
It creates `txspike-new-<stamp>` and `txspike-existing-<stamp>` in the workspace from
`samples/deploy-qa`, with a broken copy where `Sales[Total Amount]` is `SUM ( Sales[Nope] )`,
and deletes both at the end (`--keep` leaves them). The report is printed and written to
`%TEMP%\tx-deploy-transaction-spike-<stamp>.md`.

## Scenarios

| | What it does | What it answers |
|---|---|---|
| A0 | `BeginTransaction` / `RollbackTransaction` / `CommitTransaction` on an idle session | Does the endpoint accept explicit transactions at all? |
| A1 | New database: begin, deploy broken model, inspect, `refresh calculate`, inspect, roll back, look again from a fresh connection | Are uncommitted states visible mid-transaction? Does rollback leave no database behind? |
| A2 | Existing database (good model committed first): begin, deploy broken model, inspect, calculate, inspect, roll back, look again | Same, and does rollback restore the previous definition? |
| A3 | Control: as A2 but commit | Confirms the post-commit state `#237` reports, for comparison with the mid-transaction read |

Each mid-transaction inspection reads twice: AMO (`server.Refresh()` + `database.Refresh(true)`,
what `TomDeployValidator` uses) and the `$SYSTEM.TMSCHEMA_MEASURES` DMV over ADOMD bound to
the same session ID.
