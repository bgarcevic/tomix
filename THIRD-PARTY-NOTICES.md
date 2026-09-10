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

## Vendored DAX language engine

The DAX lexer, parser, and syntax classifier under `src/Tomix.Core/Dax/Engine/` are **used and
adapted from the SQLBI Whiteboard DAX engine**, used under the MIT License.

- License: MIT — Copyright (c) 2026 SQLBI Corp.

  > Permission is hereby granted, free of charge, to any person obtaining a copy of this software
  > and associated documentation files (the "Software"), to deal in the Software without
  > restriction, including without limitation the rights to use, copy, modify, merge, publish,
  > distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
  > Software is furnished to do so, subject to the following conditions:
  >
  > The above copyright notice and this permission notice shall be included in all copies or
  > substantial portions of the Software.
  >
  > THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
  > BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
  > NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
  > DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
  > OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

The files carry the copyright header and are adapted to this repository (namespaces, the doubled
closing-bracket escape in the lexer's bracket scan, and comment rewording). The `Tomix.Core.Dax`
facade, the offline `DaxSyntaxCheck` analyzer, and the offline DAX formatter that powers
`tx format` (`DaxCodeFormatter`/`DaxPrinter`/`Doc`) are tomix code built on that engine.

## NuGet dependencies (redistributed)

| Package | Version | License | Copyright / Project |
|---------|---------|---------|---------------------|
| System.Linq.Dynamic.Core | 1.7.2 | Apache-2.0 | Copyright (c) ZZZ Projects / Stef Heyenrath — <https://github.com/zzzprojects/System.Linq.Dynamic.Core> |
| Dax.Metadata / Dax.Model.Extractor / Dax.ViewModel / Dax.Vpax (VertiPaq-Analyzer) | 1.12.1 | MIT | SQLBI, Copyright (c) Marco Russo — <https://github.com/sql-bi/VertiPaq-Analyzer> |
| Dax.Vpax.Obfuscator | 1.2.1 | MIT | SQLBI — <https://github.com/sql-bi/Vpax-Obfuscator> |
| Microsoft.AnalysisServices (TOM) | 19.114.0 | Microsoft Software License Terms (redistributable client libraries) | Copyright (c) Microsoft Corporation — <https://www.nuget.org/packages/Microsoft.AnalysisServices> |
| Microsoft.AnalysisServices.AdomdClient | 19.114.0 | Microsoft Software License Terms (redistributable client libraries) | Copyright (c) Microsoft Corporation — <https://www.nuget.org/packages/Microsoft.AnalysisServices.AdomdClient> |
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
