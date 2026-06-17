# Third-Party Notices

`tomix-cli` is licensed under the [MIT License](LICENSE). It redistributes, depends on, or derives
from the third-party components below. Each remains under its own license, reproduced or linked here
to satisfy that license's attribution requirements.

## Bundled Best Practice Analyzer rules

The bundled rule set at `src/Tomix.App/Bpa/Rules/bpa-rules.json` is **derived from the Microsoft
Analysis Services Best Practice Rules** (the standard Tabular/Power BI BPA ruleset, authored and
maintained primarily by Michael Kovalsky and Microsoft).

- Source: <https://github.com/microsoft/Analysis-Services/tree/master/BestPracticeRules>
- License: MIT — Copyright (c) Microsoft Corporation.

The `tx bpa` engine and CLI are an independent implementation. The
`BestPracticeAnalyzer*` annotation keys, the rule JSON schema, and the dynamic-LINQ expression
dialect are interoperability conventions (also used by Tabular Editor) so that models authored
elsewhere work with `tx`; **no Tabular Editor source code is used or included.** Tabular Editor 2 is
itself MIT-licensed (Copyright (c) Tabular Editor ApS).

## NuGet dependencies (redistributed)

| Package | Version | License | Copyright / Project |
|---------|---------|---------|---------------------|
| System.Linq.Dynamic.Core | 1.7.2 | Apache-2.0 | Copyright (c) ZZZ Projects / Stef Heyenrath — <https://github.com/zzzprojects/System.Linq.Dynamic.Core> |
| Dax.Formatter | 1.2.0 | MIT | SQLBI — <https://github.com/sql-bi/DaxFormatter> |
| Microsoft.AnalysisServices (TOM) | 19.114.0 | Microsoft Software License Terms (redistributable client libraries) | Copyright (c) Microsoft Corporation — <https://www.nuget.org/packages/Microsoft.AnalysisServices> |
| Spectre.Console | 0.55.2 | MIT | Copyright (c) Patrik Svensson, Phil Scott, Nils Andresen — <https://github.com/spectreconsole/spectre.console> |
| System.CommandLine | 2.0.8 | MIT | Copyright (c) .NET Foundation — <https://github.com/dotnet/command-line-api> |
| Microsoft.Identity.Client (MSAL) | 4.66.2 | MIT | Copyright (c) Microsoft Corporation — <https://github.com/AzureAD/microsoft-authentication-library-for-dotnet> |
| Microsoft.Identity.Client.Extensions.Msal | 4.66.2 | MIT | Copyright (c) Microsoft Corporation — (same project as above) |
| System.Security.Cryptography.ProtectedData | 9.0.0 | MIT | Copyright (c) .NET Foundation — <https://github.com/dotnet/runtime> |

`MinVer` (build-time only, not redistributed) and the test-only packages (`xunit`,
`Microsoft.NET.Test.Sdk`, `coverlet.collector`) are not shipped in released artifacts and are
omitted here.

### Apache-2.0 components

`System.Linq.Dynamic.Core` is licensed under the Apache License, Version 2.0. A copy of the license
is available at <https://www.apache.org/licenses/LICENSE-2.0>. The project distributes no separate
`NOTICE` file; no additional notices are required beyond this attribution.

---

If you believe an attribution is missing or incorrect, please open an issue.
