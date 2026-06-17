# Bundled BPA rules

`bpa-rules.json` is the default Best Practice Analyzer rule set used by `tx bpa` when no other
ruleset is selected.

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
