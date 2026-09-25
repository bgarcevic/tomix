# Refresh-policy QA

One import table with a basic incremental-refresh policy and inline M data; no
external connections. Both the seed partition and policy source return the query
filtered by `RangeStart` and `RangeEnd`.

Use effective date **2024-09-01**. The three-year archive includes two 2024 rows
(EventCount total **13**), excludes the expired 2020 row and the future 2025 row.
The two-month incremental window includes only the August row (total **3**).

Deploy a copy under a unique `TomixQa_<run-id>` name in a sandbox. Rename the table
in that copy to `QA_<run-id>_Events`. Keep the effective date fixed throughout QA.

1. Preview and execute `refresh --table <table> --policy-only --effective-date 2024-09-01`.
   Expect eight partitions: `2021`, `2022`, `2023`, `2024Q1`, `2024Q2`, `2024Q307`,
   `2024Q308`, `2024Q309`. All are unprocessed; no data has loaded.
2. Apply the same policy again. Expect no partition changes.
3. Run normal policy refresh with `--apply-refresh-policy true --effective-date 2024-09-01`.
   Only the incremental window loads: `COUNTROWS` = **1**, `SUM(EventCount)` = **3**.
   Historical partitions remain unprocessed after an empty bootstrap.
4. Backfill the existing partition ranges with `--refresh-type full --skip-refresh-policy`.
   All eight partitions become ready; `COUNTROWS` = **2**, `SUM(EventCount)` = **13**.

Pass `-s <workspace> -d <model>` for every remote command. Preview refreshes with
`--dry-run`; pass `--yes --non-interactive` when executing partition-risky operations.
Initial local parameters cover 2021 through September 2024.
