# samples

Small sample models used by tests and documentation.

## Contents

- `basic-tmdl/` — minimal TMDL folder model (3 tables, 4 measures); the primary test fixture.
- `basic-tmdl.bim` — the same model as a single BIM file.
- `Artificial Intelligence Sample.*` — a PBIP project (report + semantic model) for realistic-scale scenarios.

## Responsibilities

- Provide tiny, understandable examples.
- Support tests and documentation.
- Show realistic usage without requiring private data.

## Cross-folder dependencies

- Used by `/tests/Tomix.Provider.Tom.Tests`.
- Used by `/tests/Tomix.Cli.Tests` (output-contract tests run against `basic-tmdl`).
- Referenced by `/docs`.
- Should remain independent of private services or credentials.

## Rules

- Do not include secrets.
- Do not include customer data.
- Keep samples small.
- Prefer examples that are safe to run locally.

## Naming

- Use kebab-case folder names for new samples (e.g. `basic-tmdl`).
