# Live QA checklist

What to check against a **real deployed model** before calling a release good —
and nothing more. Everything that does not need a live endpoint belongs in the
test suite or in `scripts/qa/` (offline, self-testing harnesses); this page
covers only what those cannot prove: network behavior, server-side state
changes, permissions, and engine semantics against real data.

The fixture for this pass is [`samples/qa-fixture`](https://github.com/bgarcevic/tomix-cli/tree/main/samples/qa-fixture)
— its README records the expected manifest (counts, findings, query results)
you compare against. A check with no expected value recorded is not a check.

## Safety boundaries

1. Dedicated QA workspace/capacity only. Name the deployed database
   `TomixQa_<run-id>`; never point mutations, refreshes, or deploys at
   production or a shared model.
2. Export the pristine model (TMDL and BIM) before testing and keep the
   checksum in the run record. Restore from it between command families, then
   refresh if needed and rerun a known-value query. A clean metadata diff alone
   does not prove that processed data survived the restore.
3. Prefix every object created during the run with `QA_<run-id>_` so cleanup is
   unambiguous. Run destructive commands in preview/dry-run first; persist only
   after reviewing the preview.
4. Before every persisted mutation, record a probe proving current state; after
   it, reconnect and rerun the probe to prove persistence. Never retry a
   mutation until a fresh probe establishes whether the server committed it.
5. `--non-interactive` for scripted runs; verify confirmation behavior once
   separately in a disposable interactive session.
6. No secrets in commands, logs, issue comments, shell history, or artifacts.
   Use the masked prompt, stdin, a secret file, or managed identity. Generated
   scripts, VPAX dictionaries, and trace files carry model structure and
   endpoints — sanitize before publishing (see the warning in
   `scripts/qa/README.md`).

## The live-only checks

### Connection and target resolution

- [ ] `auth status` unauthenticated → `auth login` (one approved flow) →
      protected command works → `auth logout` → protected command fails
      cleanly; expired/invalid credential gives an actionable error.
- [ ] Target precedence: explicit `-s/-d` beats profile beats active connection
      beats recent. Use two distinct models so a wrong resolution is visible.
- [ ] A failed `connect` leaves the prior active connection unchanged; two
      sessions with different `TOMIX_SESSION` values don't leak state.
- [ ] Unreachable server / DNS failure / timeout: error is actionable, exit
      code documented, no partial local state.

### Read semantics against real data

- [ ] `load`/`ls` counts match the fixture manifest.
- [ ] `query` against the fixture returns the manifest values (`Total Sales`
      = 896.49; row counts and ordering per the `dax-tests` files); JSON and
      CSV stdout parse with an independent parser.
- [ ] `query --trace` / `--plan` / `--cold` with an admin identity: expected
      column sets (#94). With a non-admin identity: warning plus a successful
      rowset, never a failure.
- [ ] Repeated and concurrent reads are stable; no read changes server
      metadata, local baselines, or active connection.

### Mutations and persistence

- [ ] One representative `add`/`set`/`rm` per family: preview leaves both
      server and local state unchanged; stage shows in `stage list`; `--save`
      persists and a fresh connection observes it; revert/discard restores the
      baseline.
- [ ] Rename with reference fixup on `Sales[Amount]` (referenced by DAX) and on
      an RLS-referenced column (fixup must refuse or warn per `--strict-refs`).
- [ ] `rm` of a DAX-referenced object is blocked; `--force` only on a
      disposable variant, and reports the broken references.
- [ ] Interrupted save / Ctrl-C during persistence leaves old or new valid
      content, never a truncated hybrid.

### Refresh and incremental refresh (real engine only)

- [ ] `refresh --dry-run` script parses and validates independently; a bounded
      full and table refresh completes and updates a timestamp probe.
- [ ] `refresh --table <table> --policy-only` with a fixed `--effective-date`: generated partitions appear on a fresh connection with
      stable names; repeat apply is idempotent. Restore the fixture explicitly afterward;
      removing a policy alone leaves its partitions in place.
- [ ] Cancel one long refresh; reconnect, classify final server state, and only
      then decide on retry.

### Deploy

- [ ] Dry-run of no-op and known-change deployments matches what a real deploy
      executes; `--create-only` refuses on an existing target.
- [ ] Deploy the fixture, verify object counts and baseline queries on the
      target, then record one failed-deploy recovery (missing permission or
      network loss) with a server probe before any retry.

### Permissions and denial paths

- [ ] Both personas from the fixture: admin-capable identity passes
      trace/plan paths; least-privilege identity gets actionable
      permission-denied diagnostics on mutation/refresh, with no partial
      mutation.

### Platform and install

- [ ] Run the read + connect sections once per supported OS you can reach
      (Windows, macOS, Linux); shell quoting and path separators exercised via
      the fixture paths.
- [ ] `update --check` against an installed build; actual update only in a
      disposable install.

## Run record

One block per run, kept with the artifacts:

```
date/utc, tester, commit + tx --version, install method,
os/arch/shell/.net, workspace (sanitized), fixture checksum,
auth method + effective permission, pass/fail/blocked counts, artifact path
```

Failures become issues with: sanitized command, expected/actual, exit code,
before/after probes, reproducibility count. A screenshot alone is not
evidence.

### Refresh-policy bootstrap and data loading

Use a disposable deployed model with a saved policy and a working data source.

- [ ] Capture baseline partitions and row counts; run policy-only with `--dry-run`
      and a fixed `--effective-date`; verify no partitions or data changed.
- [ ] Execute `tx refresh --table Sales --policy-only --effective-date 2026-01-01
      -s <workspace> -d <test-model> --yes`; verify generated partitions exist
      without loaded data and inspect any removed expired partitions.
- [ ] Run `tx refresh --table Sales --apply-refresh-policy true
      --effective-date 2026-01-01 -s <workspace> -d <test-model> --yes`;
      verify the incremental window loads; historical partitions can remain empty.
- [ ] Backfill with `--refresh-type full --skip-refresh-policy`; verify all
      partitions become ready and the full archive row count matches the fixture.
- [ ] Record target, policy, effective date, operation results, and row counts.
      Skip live execution when no suitable disposable deployed model is available.
