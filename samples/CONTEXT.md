# samples

Small sample models, rules, tests, and CI examples.

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

- Use kebab-case folder names.
- Example: `basic-bim`, `basic-tmdl`, `github-actions`.
