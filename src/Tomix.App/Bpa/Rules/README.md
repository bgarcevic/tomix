# Bundled BPA rules

`bpa-rules.json` is the bundled Best Practice Analyzer rule catalog. The default `standard`
ruleset used by `tx bpa` is a curated high-signal subset of this catalog (see `CuratedRuleIds` in
`BpaRuleLoader`); `--ruleset full` selects the entire catalog except the default-off categories
(`DefaultOffCategories`), which each have their own `--ruleset` preset.

## Catalog policy

- Keep the default set small and high-signal. A new rule joins `standard` only if it rarely fires
  on a healthy model; error severity there is reserved for a broken model, because the deploy gate
  blocks on it.
- Grow the catalog through default-off categories. A category that would bury real findings on
  most models (for example, localization rules on a single-culture model) goes in
  `DefaultOffCategories` and is opted into with its preset: `--ruleset standard,localization`.
- The two `IsAvailableInMdx` rules point the same way: `true` on necessary columns is an
  error-prevention rule in `standard`, and `false` on non-attribute columns is an opt-in
  optimization that excludes every column the first rule protects. `BpaCatalogPolicyTests` pins
  this. Don't add a "false on every hidden column" variant.

## Provenance & license

These rules are **derived from the Microsoft Analysis Services Best Practice Rules** — the standard
Tabular / Power BI BPA rule set authored and maintained primarily by Michael Kovalsky and Microsoft:

- <https://github.com/microsoft/Analysis-Services/tree/master/BestPracticeRules>
- Licensed under the **MIT License**, Copyright (c) Microsoft Corporation.

The rule **format** (the `ID` / `Name` / `Category` / `Severity` / `Scope` / `Expression` /
`FixExpression` / `CompatibilityLevel` JSON schema and the dynamic-LINQ expression dialect) is an
interoperability convention shared with Tabular Editor, so rule files authored for that ecosystem can
be loaded directly. No third-party analyzer source code is included — see
[`THIRD-PARTY-NOTICES.md`](../../../../THIRD-PARTY-NOTICES.md) at the repository root.

## Updating

`BpaRuleLoader` can also fetch Microsoft's canonical set on demand (e.g. `tx bpa run --ruleset
microsoft`). When editing the bundled copy, keep each rule's `ID` stable — rule IDs are the keys used
by ignore/disable state and by precedence de-duplication across rule sources.
