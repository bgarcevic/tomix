# Deploy QA harness

Offline gate for `tx deploy`'s granular deployment. `deploy --xmla` emits the exact TMSL a
real deploy would execute, so most edge cases can be hunted by inspecting payloads instead
of spending live deploys. Nothing here executes a deploy — script generation is read-only
against the target, so the whole harness is safe to run against a real workspace.

**The output is not safe to publish.** A generated script carries the full structure of
whatever model you pointed at — every table, column and measure name, the DAX and M behind
them, and any endpoint hostnames held in shared expressions. Against a customer's model that
is their schema and infrastructure. `deploy-qa/` and `*.xmla` are gitignored for this reason;
do not defeat that, and prefer an `--out` outside the repo when the model is not a sample.
Note that the credential scan below deliberately does not flag endpoints — a script that
deploys connections is supposed to contain them — so "0 failures" says nothing about whether
the file is publishable.

| Script | Does |
|--------|------|
| `deploy-script-matrix.sh` | Generates one script per `--deploy-*` combination, checks each, and asserts the invalid combinations are rejected. |
| `check-deploy-script.sh` | Asserts invariants on a single generated script. Usable standalone on any `--xmla` output. |
| `diverge-deploy-qa.sh` | Writes a copy of `samples/deploy-qa` that differs in every preservable aspect, so the checks can be conclusive. |
| `selftest-checker.sh` | Mutates a clean script once per invariant and proves the checker catches it. Also proves the divergence still applies. Fully offline. |

## Run it

```sh
scripts/qa/deploy-script-matrix.sh \
  --model ./MyModel.SemanticModel \
  --server "powerbi://api.powerbi.com/v1.0/myorg/sandbox" \
  --database "MyDataset" \
  --out ./deploy-qa
```

Cells generated: `full` (the pure-source reference), `default`, `connections`, `partitions`,
`partitions-policy`, `expressions`, `roles`, `roles-members`. Rejected combinations checked:
`--deploy-full` with a granular flag, `--deploy-policy-partitions` alone,
`--deploy-role-members` alone — each must exit 2 with a `TOMIX_DEPLOY_INVALID_FLAGS` JSON
envelope, since that code is what a pipeline branches on.

The 6 granular flags are 64 combinations; the matrix covers each flag alone, both dependent
pairs, plus default and full. The rest are independent — a cross-product buys nothing.

`--strict` turns an inconclusive preservation check into a failure; see
[Making the differentials conclusive](#making-the-differentials-conclusive) for the fixture it
expects.

The run defaults to the repo's `./tx`, which rebuilds from source on every call, and echoes
which executable it used. Pass `--tx tx` to QA the installed global tool instead — but note
that it is whatever was last packed, so it may not be the code you are trying to test. Use
`-- --profile prod` to pass anything else through to `tx`.

Diffing two cells shows exactly what a flag changes:

```sh
diff <(jq -S . deploy-qa/full.xmla) <(jq -S . deploy-qa/default.xmla)
```

## What the checks catch

Structural — hold for every cell, no reference needed:

| Invariant | Bug class |
|---|---|
| Addresses the target by name; payload name pinned to it | Deploying to the wrong database; Power BI rejecting a rename |
| `id` is the target's, not a copy of the name | ID churn on redeploy |
| No duplicate names in any collection, case-insensitively | A case-sensitive merge emitting two entries AS treats as one — invalid TMSL |
| Every table has ≥1 partition | A name-keyed preserve dropping partitions for a table with no target counterpart |
| Every partition binds to a data source / expression that exists | A preserved partition outliving a renamed source |
| Every relationship endpoint resolves to a table | A merge dropping a table but keeping its relationships |
| No `memberId` (cloud targets) | Stale service-assigned ids failing redeploy |
| No credential-shaped strings | The untested redaction path in issue #123 — reported as WARN for eyeballing, since names can legitimately contain these words |

Differential — needs `--reference full.xmla`, which the matrix wires up automatically:

- **Structure outside the preserved aspects is byte-identical to the source.** The broadest
  guard here: any splice reaching past its own aspect fails this, for every flag combination,
  without needing to know the target's state.
- Each aspect: deployed ⇒ must equal the source; preserved ⇒ must differ from it.
- No source-side data source or expression went missing (preserving still deploys entries
  that are new in the source).
- Role definitions and role membership are compared separately, because they deploy
  separately: `--deploy-roles` takes definitions from the source while members still come
  from the target.
- Partitions are compared as two populations, because they have different contracts: ordinary
  tables must take source partitions under `--deploy-partitions`, while a table whose refresh
  policy carries a `sourceExpression` keeps the target's until `--deploy-policy-partitions` is
  passed too. A table either side has under policy is treated as protected, so the exemption
  is never reported as a preservation failure.

## Making the differentials conclusive

A preserved aspect that happens to match the source reports `WARN … inconclusive` — the check
cannot tell "preserved correctly" from "preservation did nothing." **A warning does not fail a
cell**, so a matrix over a model that has no roles and no policy tables reports every cell
clean while proving almost none of them. That is the trap this section exists to close: read
the per-cell output, not the summary, or pass `--strict` and let inconclusive results fail.

`samples/deploy-qa` is built for this. It carries one of everything preservation touches:
four shared expressions (two environment-specific, plus the `RangeStart`/`RangeEnd` the policy
needs), two roles — one with a table permission — a plain import table, a table with two
manual partitions, a refresh-policy table, and a calculated table. Nothing in it points at a
real service, so its generated scripts are safe to keep.

```sh
# 1. seed the target from the pristine fixture (a live deploy, and the only one here)
tx deploy samples/deploy-qa \
  --server "powerbi://api.powerbi.com/v1.0/myorg/sandbox" --database deploy-qa \
  --deploy-full --skip-bpa --yes

# 2. add one member to the QA Reader role on the target, in the portal's Security dialog.
#    Members are the one aspect the fixture cannot carry: it would mean a real principal in
#    a public repo. Without this step the two role-member cells stay inconclusive.

# 3. write a source that differs from that target in every preservable aspect
scripts/qa/diverge-deploy-qa.sh --out /tmp/deploy-qa-diverged

# 4. run the matrix against the seeded target, with inconclusive results failing
scripts/qa/deploy-script-matrix.sh \
  --model /tmp/deploy-qa-diverged \
  --server "powerbi://api.powerbi.com/v1.0/myorg/sandbox" --database deploy-qa \
  --out /tmp/deploy-qa --strict
```

Expect one `info … none in this model` for data sources, and no other coverage gap. Power BI
and Fabric models set `defaultPowerBIDataSourceVersion: powerBI_V3`, which has no `dataSource`
objects at all — connections live in M expressions — so against those targets there is nothing
for `--deploy-connections` to preserve and the cell is structurally vacuous. To exercise it you
need an Azure AS or SSAS target and a fixture that drops `compatibilityMode: powerBI` from
`database.tmdl` and adds a `dataSources.tmdl`:

```
dataSource SQL/localhost;QaWarehouse
	connectionDetails =
			{
			  "authentication": null,
			  "query": null
			}
		protocol: tds
		address
			server: localhost
			database: QaWarehouse
```

Until then, `PreserveDataSources` is covered by `TmslDeployScriptBuilderTests` only.

Re-run `selftest-checker.sh` after editing the fixture. It regenerates the divergence and
fails if any aspect stopped differing — an innocent-looking edit to `samples/deploy-qa` (say,
setting `Environment` to the value the divergence changes it to) silently turns a live cell
inconclusive.

## What this cannot prove

The payload being well-formed and containing what you intended is not the engine accepting
it — Analysis Services validation lives in their code. Still needs a live target:

- Engine acceptance of a spliced payload
- Processed data surviving a preserving deploy (compare `COUNTROWS` before/after)
- Incremental-refresh partitions surviving `--deploy-partitions` and being discarded by
  `--deploy-policy-partitions`. The fixture proves the *payload* keeps them; only a refreshed
  target shows the data does. Refresh `deploy-qa` after seeding to get real policy partitions.
- Bound credentials surviving (change a connection string in the portal, deploy, refresh)
- ID pinning actually preventing churn across two deploys
- Sync-back paths still deploying `Full` (a `tx set` edit must not be preserved away)

Run this harness first; spend live deploys on that list.
