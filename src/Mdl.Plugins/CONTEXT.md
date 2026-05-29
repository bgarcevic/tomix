# Mdl.Plugins

Future plugin system.

## Responsibilities

- Plugin discovery.
- Plugin metadata.
- Future extension points for rules, commands, providers, and output.

## Cross-folder dependencies

- Depends on `/src/Mdl.Core`.
- May expose extension points for `/src/Mdl.Rules`, `/src/Mdl.Provider.*`, and `/src/Mdl.Output`.
- Must not become required for normal CLI operation.
- Must not create circular dependencies between feature projects.

## Rules

- Do not build plugin complexity before core commands are stable.
- Keep plugin APIs explicit and versioned.
- Plugins must not weaken safety rules.

## Current Status

This area is reserved for future work.
