using System.Text;
using Tomix.App.ModelObjects;
using Tomix.App.Models;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Test;

/// <param name="Path">The test folder to create or add to.</param>
/// <param name="FromModel">Also write smoke tests built from this model's measures and tables; null for the generic scaffold only.</param>
/// <param name="Force">Overwrite scaffold files that already exist with other content.</param>
public sealed record TestInitRequest(string Path, ModelReference? FromModel = null, bool Force = false);

/// <summary>Property names and order are the documented JSON contract (additive changes only).</summary>
/// <param name="Path">The test folder.</param>
/// <param name="Created">Files written, relative to <paramref name="Path"/> ('/'-separated).</param>
/// <param name="Skipped">Files left alone because they already hold exactly the scaffold's content.</param>
/// <param name="NextSteps">Commands to run next, in order.</param>
public sealed record TestInitResult(
    string Path,
    IReadOnlyList<string> Created,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> NextSteps);

/// <summary>
/// Scaffolds a <c>tx test</c> folder: a README, a connection smoke test, and with a model, smoke
/// tests over its measures and tables. It reads the model (offline for TMDL/BIM) but never runs
/// a query. Every test it writes is decided by <c>tx:</c> assertions, so none needs a snapshot and
/// none breaks when the data refreshes. Existing files are never overwritten without
/// <see cref="TestInitRequest.Force"/>.
/// </summary>
public sealed class TestInitHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public TestInitHandler(IEnumerable<IModelProvider> providers)
        => _providers = providers.ToList();

    public async Task<TomixResult<TestInitResult>> HandleAsync(TestInitRequest request, CancellationToken cancellationToken)
    {
        if (File.Exists(request.Path))
            return TomixResult<TestInitResult>.Fail(
                "TOMIX_TEST_INIT_NOT_A_DIRECTORY",
                $"Not a directory: {request.Path}",
                exitCode: 2,
                hint: "Pass the folder to create the tests in, for example 'tx test init ./tests'.");

        if (request.FromModel is null)
            return Write(request, Scaffold(request.Path, snapshot: null));

        return await ModelSessionRunner.RunAsync(_providers, request.FromModel, async session =>
        {
            var snapshot = await session.GetSnapshotAsync(cancellationToken);
            return Write(request, Scaffold(request.Path, snapshot));
        }, cancellationToken);
    }

    private static TomixResult<TestInitResult> Write(TestInitRequest request, Scaffolded scaffold)
    {
        var created = new List<string>();
        var skipped = new List<string>();
        var conflicts = new List<string>();
        foreach (var (relative, content) in scaffold.Files)
        {
            var full = Path.Combine(request.Path, relative);
            if (!File.Exists(full))
                created.Add(relative);
            else if (File.ReadAllText(full) == content)
                skipped.Add(relative);
            else if (request.Force)
                created.Add(relative);
            else
                conflicts.Add(relative);
        }

        if (conflicts.Count > 0)
            return TomixResult<TestInitResult>.Fail(
                "TOMIX_TEST_INIT_EXISTS",
                $"These files already exist in {request.Path} with other content: {string.Join(", ", conflicts)}",
                exitCode: 2,
                hint: "Pass --force to overwrite them, or scaffold into another folder.");

        try
        {
            foreach (var relative in created)
            {
                var full = Path.Combine(request.Path, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, scaffold.Files[relative], new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TomixResult<TestInitResult>.Fail(
                "TOMIX_TEST_INIT_WRITE_FAILED",
                $"Could not write the test scaffold: {ex.Message}",
                exitCode: 1);
        }

        return TomixResult<TestInitResult>.Ok(new TestInitResult(request.Path, created, skipped, scaffold.NextSteps));
    }

    private sealed record Scaffolded(IReadOnlyDictionary<string, string> Files, IReadOnlyList<string> NextSteps);

    private static Scaffolded Scaffold(string path, ModelSnapshot? snapshot)
    {
        // Raw literals take the source file's line endings; the scaffold is "\n" on every OS.
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["README.md"] = Readme.ReplaceLineEndings("\n"),
            ["smoke/connection.dax"] = ConnectionTest.ReplaceLineEndings("\n"),
        };

        if (snapshot is not null)
        {
            if (HasMeasures(snapshot))
                files["smoke/measure-errors.dax"] = MeasureErrorsTest.ReplaceLineEndings("\n");
            if (TableRows(snapshot) is { } rows)
                files["smoke/table-rows.dax"] = rows;
        }

        return new Scaffolded(files, [$"tx test {path.Replace('\\', '/')}"]);
    }

    private const string ConnectionTest =
        """
        // Proves the model answers queries. The tx: line asserts on the result, so this test
        // needs no snapshot.
        // tx: tags=smoke rows=1
        EVALUATE ROW ( "Ok", 1 )

        """;

    /// <summary>Whether the scaffold gets the broken-measure test; hidden measures count too.</summary>
    internal static bool HasMeasures(ModelSnapshot snapshot)
        => ModelObjectProjection.Flatten(snapshot).Any(obj => obj.Kind == ModelObjectKind.Measure);

    /// <summary>
    /// Lists the measures the engine reports an error for, asserted to be none. It reads model
    /// metadata only, so no measure is calculated and its cost does not grow with the data.
    /// </summary>
    private const string MeasureErrorsTest =
        """
        // Lists the measures whose DAX is broken: a syntax error, or a reference to a column or
        // measure that no longer exists. It reads the model's metadata and calculates no measure,
        // so it stays cheap on any model size. It needs INFO functions (Power BI, Fabric or a
        // recent Analysis Services). To see which measures are broken, run it with 'tx query'.
        // tx: tags=smoke rows=0
        EVALUATE
        SELECTCOLUMNS (
            FILTER ( INFO.MEASURES (), NOT ISBLANK ( [ErrorMessage] ) ),
            "Measure", [Name],
            "Error", [ErrorMessage]
        )

        """;

    /// <summary>
    /// The tables that have no rows, asserted to be none, so it passes without a snapshot and
    /// survives data refreshes. Calculation groups are left out. Null when the model has no table.
    /// </summary>
    internal static string? TableRows(ModelSnapshot snapshot)
    {
        var tables = snapshot.Objects
            .Where(obj => obj.Kind == ModelObjectKind.Table
                && !obj.Children.Any(child => child.Kind == ModelObjectKind.CalculationItem))
            .Select(obj => obj.Name)
            .ToList();
        if (tables.Count == 0)
            return null;

        var dax = new StringBuilder();
        dax.Append("// Lists the tables that have no rows; passes when there are none, so a refresh that\n");
        dax.Append("// loads nothing fails it. To see which tables are empty, run it with 'tx query'.\n");
        dax.Append("// tx: tags=smoke rows=0\n");
        dax.Append("EVALUATE\nFILTER (\n    {\n");
        dax.Append(string.Join(",\n", tables.Select(name => $"        ( {StringLiteral(name)}, COUNTROWS ( {QuoteTable(name)} ) )")));
        dax.Append("\n    },\n    ISBLANK ( [Value2] )\n)\n");
        return dax.ToString();
    }

    private static string StringLiteral(string text) => $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string QuoteTable(string name) => $"'{name.Replace("'", "''", StringComparison.Ordinal)}'";

    private const string Readme =
        """
        # Model tests

        DAX regression tests for `tx test`. Each test is a `.dax` file holding one `EVALUATE`
        query. Run them against a deployed model or a local instance:

        ```sh
        tx test ./tests -s <workspace> -d <model>
        ```

        ## How a test is decided

        - **Snapshot** (the default): the result is compared with a sibling `<name>.expected.json`.
          A test without one fails as `MISS` until you record it:

          ```sh
          tx test ./tests --update-missing   # record snapshots for new tests
          tx test ./tests --update           # accept intended changes, then review the git diff
          ```

        - **Assertions**: `tx:` comment lines before the query check the result instead, so the
          test needs no snapshot and survives data refreshes:

          ```dax
          // tx: rows>=1
          // tx: columns=Sales[Region], [Total Sales]
          EVALUATE SUMMARIZECOLUMNS ( Sales[Region], "Total Sales", [Total Sales] )
          ```

          Add `snapshot` to keep a snapshot as well.

        - **Expected error**: `// tx: expect-error=cannot be found` passes only when the query fails
          with a matching error.

        ## Directives

        | Directive | Effect |
        |-----------|--------|
        | `tolerance=0.01` / `tolerance=0.1%` | Accept numeric differences this small. |
        | `ignore-row-order` | Compare rows in any order. |
        | `rows=<n>`, `rows>=<n>`, `rows<=<n>` | Assert the row count. |
        | `columns=<names>` | Assert these columns are in the result. |
        | `snapshot` | Keep a snapshot alongside the assertions. |
        | `expect-error[=<regex>]` | The query must fail. |
        | `tags=smoke,nightly` | Tag the test; run tagged tests with `--tag smoke`. |

        `tx test list ./tests` shows every test, its tags and how it is decided.

        ## CI

        ```sh
        tx test ./tests -s <workspace> -d <model> --ci github --trx results.trx
        ```

        The run exits `1` on any failing test, so the pipeline blocks the merge. Use `--ci vsts` on
        Azure Pipelines.

        """;
}
