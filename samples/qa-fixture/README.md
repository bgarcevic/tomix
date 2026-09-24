# qa-fixture

Deterministic QA fixture for live-model validation of the `tx` command surface.
It realizes the fixture spec from issue #106; the run protocol that uses it is
[docs/qa-live-checklist.md](../../docs/qa-live-checklist.md). Every object name,
row, and expression is fixed so results are comparable across runs and machines.
Partitions are inline `#table` M — no external data, fully offline to load and
validate.

`samples/qa-fixture-variant/` is the same model with exactly one structural
difference (an added measure), so `diff` has a known target.

## What the fixture covers

| Fixture requirement | Where |
|---|---|
| Fact table with deterministic import data | `Sales` (8 rows, incl. leap day 2024-02-29, a negative and a zero amount) |
| Dimension with hidden + calculated columns, RLS target | `Customer` (`InternalCode` hidden, `NameLength` calculated, `QA Reader` filters `Country = "DK"`) |
| Marked date table, hierarchy, sort-by | `Date` (`dataCategory: Time`, `isKey`, hierarchy `Calendar`, `MonthName` sorted by `MonthNumber`) |
| Multi-partition table | `Metrics` (partitions `Metrics-H1`, `Metrics-H2`) |
| Incremental refresh | `Events` (basic policy, 3-year rolling / 2-month incremental, `RangeStart`/`RangeEnd`) |
| Dependent measure | `Sales Per Customer` → `[Total Sales]` + `[Customer Count]` (cross-table) |
| Full-property measure | `Sales vs Target` (multiline DAX, description, display folder `QA\Targets`, format string, annotation, KPI) |
| Unused + hidden measure | `Metrics` / `Unused Hidden Measure` |
| Inactive relationship | `Sales_Date_Ship` (`Sales[ShipDate]` → `Date[Date]`, `isActive: false`) |
| Perspective | `SalesOnly` (Sales + partial Date) |
| Roles | `QA Reader` (RLS), `QA Admin` (no filters) |
| Culture | `en-US` with linguistic metadata |
| Shared expressions | `RangeStart`, `RangeEnd` (parameters), `QaVersion` (plain) |
| Deliberate BPA findings | see below |
| Diff variant | `qa-fixture-variant` (adds measure `Variant Delta Check`) |
| DAX regression tests | `dax-tests/*.dax` (record snapshots after deploy with `tx test ./dax-tests --update`) |

## Expected manifest (pristine fixture)

Offline load summary (`tx load samples/qa-fixture`): 5 tables, 5 measures, 22
columns, 3 relationships. After deploying to Fabric, the engine adds one hidden
`RowNumber-...` column to each table. Remote `tx load` therefore reports 27 raw
columns, while `tx ls` still lists the 22 model columns (5 Sales, 6 Customer,
5 Date, 3 Metrics, 3 Events).

- `tx validate` — clean, exit 0.
- `tx bpa run` (standard ruleset) — exactly 3 rules / 6 findings, 0 errors. Assumes the
  IsAvailableInMDX property-key fix (#251, merged to main); on builds from before it, an
  error-severity `SET_ISAVAILABLEINMDX_TO_TRUE_ON_NECESSARY_COLUMNS` ×3 false positive appears
  instead and the hidden-column rule below stays dark:
  - `AVOID_FLOATING_POINT_DATA_TYPES` ×1 (warning) — **deliberate**: `Sales[Amount]` is `double`.
  - `HIDE_FOREIGN_KEYS` ×4 (warning) — **deliberate auto-fixable family**: relationship-end columns (`Sales[OrderDate]`, `Sales[ShipDate]`, `Sales[CustomerID]`, `Customer[CustomerID]`). Use for fix → preview → stage → save QA.
  - `ISAVAILABLEINMDX_FALSE_NONATTRIBUTE_COLUMNS` ×1 (warning) — **deliberate auto-fixable**: the hidden `Customer[InternalCode]` sits at the default `true`; best practice sets hidden non-attribute columns to `false`, and the fix persists (non-default value) once applied.
- `tx deps [Sales Per Customer]` — upstream exactly 2: `[Total Sales]`, `[Customer Count]`.
- `tx deps --unused` — 14 objects, including the deliberate `Metrics/Unused Hidden Measure`, `Customer/InternalCode`, `Customer/NameLength`. (`Sales vs Target` counts as unused too: nothing references it downstream.)
- `tx incremental-refresh show Events` — rollingWindow 3 Year, incremental 2 Month, offset 0.
- `tx diff samples/qa-fixture samples/qa-fixture-variant` — exactly 1 added measure (`Sales/Variant Delta Check`), exit 1; identical models exit 0.

## DAX tests (live only)

`tx test` needs a deployed model. After deploying the fixture, record once with
`tx test ./dax-tests --update -s <workspace> -d <database>` and commit nothing —
snapshots are run artifacts (they encode server values), not repo content.

- `customers.dax` — 5 rows ordered by `CustomerID`.
- `total-sales.dax` — one row per date with sales; `Total Sales` sums to 896.49.
- `sales-per-customer.dax` — one row per country ordered by country; exercises the cross-table measure dependency.

## Deploying

```sh
tx deploy samples/qa-fixture -s <qa-workspace> -d TomixQa_<run-id> --dry-run
tx deploy samples/qa-fixture -s <qa-workspace> -d TomixQa_<run-id>
```

Do not point mutations at shared or production resources; the live checklist
defines the boundaries. Deployed database name should carry the run ID so
cleanup is unambiguous.
