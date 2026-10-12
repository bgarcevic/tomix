using Tomix.App.Bpa;
using Tomix.Core.Bpa;
using Tomix.Core.Models;
using Tomix.Core.Results;
using Tomix.Core.Rules;
using Tomix.Provider.Tmdl;

namespace Tomix.App.Tests;

/// <summary>
/// The DAX-rewrite fix intrinsics behind <c>DAX_COLUMNS_FULLY_QUALIFIED</c>
/// (<c>QualifyColumnReferences()</c>) and <c>DAX_MEASURES_UNQUALIFIED</c>
/// (<c>UnqualifyMeasureReferences()</c>): references are rewritten by span with the same
/// resolution the rules detect with, and anything ambiguous fails the object rather than being
/// guessed (#267).
/// </summary>
public sealed class BpaDaxRewriteFixTests
{
    // basic-tmdl: Sales[Amount] is the only Amount column; CustomerID exists in Sales and
    // Customers; 'Total Sales' is a Sales measure.
    [Theory]
    [InlineData("QualifyColumnReferences()", "SUMX(Sales, [Amount])", "SUMX(Sales, 'Sales'[Amount])")]
    [InlineData("QualifyColumnReferences()", "[Amount] /* keep */ + [Total Sales]", "'Sales'[Amount] /* keep */ + [Total Sales]")]
    [InlineData("QualifyColumnReferences()", "SUMX(ADDCOLUMNS(Sales, \"@x\", 1), [@x])", "SUMX(ADDCOLUMNS(Sales, \"@x\", 1), [@x])")]
    [InlineData("UnqualifyMeasureReferences()", "'Sales'[Total Sales] * 2", "[Total Sales] * 2")]
    [InlineData("UnqualifyMeasureReferences()", "Sales[Total Sales] + SUM(Sales[Amount])", "[Total Sales] + SUM(Sales[Amount])")]
    [InlineData("unqualifymeasurereferences()", "-- 'Sales'[Total Sales]\n'Sales'[Total Sales]", "-- 'Sales'[Total Sales]\n[Total Sales]")]
    public void Rewrite_RewritesOnlyTheReferencesTheRuleFlags(string fix, string before, string expected)
    {
        Assert.True(BpaDaxRewriter.TryParse(fix, out var rewrite));

        var (text, error) = BpaDaxRewriter.Rewrite(before, rewrite, Names());

        Assert.Null(error);
        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData("QualifyColumnReferences()", "COUNTROWS(FILTER(Sales, [CustomerID] > 0))", "column [CustomerID] exists in tables")]
    [InlineData("QualifyColumnReferences()", "SUMX(ADDCOLUMNS(Sales, \"Amount\", 1), [Amount])", "[Amount] may name a query-scoped column")]
    [InlineData("UnqualifyMeasureReferences()", "'Customers'[Total Sales]", "measure [Total Sales] is qualified with 'Customers' but lives in 'Sales'")]
    public void Rewrite_AmbiguousReference_FailsWithoutRewriting(string fix, string before, string reason)
    {
        Assert.True(BpaDaxRewriter.TryParse(fix, out var rewrite));

        var (text, error) = BpaDaxRewriter.Rewrite(before, rewrite, Names());

        Assert.Null(text);
        Assert.Contains(reason, error);
    }

    [Theory]
    [InlineData("Delete()")]
    [InlineData("IsHidden = true")]
    [InlineData("QualifyColumnReferences")]
    public void TryParse_OtherFixExpressions_AreNotRewrites(string fix)
        => Assert.False(BpaDaxRewriter.TryParse(fix, out _));

    [Theory]
    [InlineData("DAX_MEASURES_UNQUALIFIED", "'Sales'[Total Sales] * 2", "[Total Sales] * 2")]
    [InlineData("DAX_COLUMNS_FULLY_QUALIFIED", "SUMX(Sales, [Amount])", "SUMX(Sales, 'Sales'[Amount])")]
    public async Task Fix_RewritesMeasureDax_SavesIt_AndSecondRunIsClean(string ruleId, string dax, string expected)
    {
        using var config = new TempConfigDir();
        using var root = new TempDir();
        var model = ModelWithMeasure(root, "Rewritten", dax);

        var first = await RunAsync(config, model, ruleId, save: true);

        Assert.True(first.Success, string.Join("; ", first.Diagnostics.Select(d => d.Message)));
        var data = first.Data!;
        Assert.Equal(1, data.FixesApplied);
        Assert.Null(data.FixErrors);
        var change = Assert.Single(data.FixChanges);
        Assert.Equal("Sales/Rewritten", change.ObjectPath);
        Assert.Equal("Expression", change.Property);
        Assert.Equal(dax, change.Before);
        Assert.Equal(expected, change.After);
        Assert.Empty(data.RemainingViolations!);
        Assert.Equal(expected, await ExpressionAsync(model, "Rewritten"));

        var second = await RunAsync(config, model, ruleId, save: true);

        Assert.True(second.Success);
        Assert.Empty(second.Data!.Violations);
        Assert.Equal(0, second.Data.FixesApplied);
        Assert.Empty(second.Data.FixChanges);
    }

    [Theory]
    [InlineData("DAX_COLUMNS_FULLY_QUALIFIED", "COUNTROWS(FILTER(Sales, [CustomerID] > 0))", "column [CustomerID] exists in tables")]
    [InlineData("DAX_MEASURES_UNQUALIFIED", "'Customers'[Total Sales]", "is qualified with 'Customers' but lives in 'Sales'")]
    public async Task Fix_AmbiguousReference_ReportsFixErrorAndLeavesDaxUntouched(string ruleId, string dax, string reason)
    {
        using var config = new TempConfigDir();
        using var root = new TempDir();
        var model = ModelWithMeasure(root, "Ambiguous", dax);

        var result = await RunAsync(config, model, ruleId, save: true);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var data = result.Data!;
        Assert.Equal(0, data.FixesApplied);
        Assert.Empty(data.FixChanges);
        var error = Assert.Single(data.FixErrors!);
        Assert.StartsWith($"[{ruleId}] Sales/Ambiguous: cannot rewrite safely:", error);
        Assert.Contains(reason, error);
        Assert.Equal(dax, await ExpressionAsync(model, "Ambiguous"));
    }

    [Fact]
    public async Task Fix_BothRulesOnOneMeasure_SecondRewriteBuildsOnTheFirst()
    {
        using var config = new TempConfigDir();
        using var root = new TempDir();
        var model = ModelWithMeasure(root, "Both", "'Sales'[Total Sales] + SUMX(Sales, [Amount])");

        var result = await RunAsync(config, model, null, save: true);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Null(result.Data!.FixErrors);
        Assert.Equal(
            "[Total Sales] + SUMX(Sales, 'Sales'[Amount])",
            await ExpressionAsync(model, "Both"));
    }

    [Fact]
    public async Task Fix_QualifiesColumnsInAnRlsFilter_SavesItOnItsTablePermission()
    {
        // TablePermission is part of the rule's scope: an RLS filter is checked and rewritten
        // like a measure, on the permission's FilterExpression.
        using var config = new TempConfigDir();
        using var root = new TempDir();
        var model = ModelWithRole(root, "[Amount] > 0");

        var first = await RunAsync(config, model, "DAX_COLUMNS_FULLY_QUALIFIED", save: true);

        Assert.True(first.Success, string.Join("; ", first.Diagnostics.Select(d => d.Message)));
        var change = Assert.Single(first.Data!.FixChanges);
        Assert.Equal("Roles/Reader/Sales", change.ObjectPath);
        Assert.Equal("FilterExpression", change.Property);
        Assert.Equal("'Sales'[Amount] > 0", change.After);
        Assert.Equal("'Sales'[Amount] > 0", await FilterAsync(model));

        var second = await RunAsync(config, model, "DAX_COLUMNS_FULLY_QUALIFIED", save: true);
        Assert.Empty(second.Data!.Violations);
    }

    [Theory]
    [InlineData("'Sales'[Amount] > LEN(USERPRINCIPALNAME())", true)]
    [InlineData("'Sales'[Amount] > 0", false)]
    public async Task DynamicRlsRule_ChecksTablePermissions(string filter, bool flagged)
    {
        using var config = new TempConfigDir();
        using var root = new TempDir();
        var model = ModelWithRole(root, filter);

        // The rule ships in the full ruleset only.
        var result = await RunAsync(config, model, "CHECK_IF_DYNAMIC_ROW_LEVEL_SECURITY_(RLS)_IS_NECESSARY", save: false, ruleset: "full");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        if (!flagged)
        {
            Assert.Empty(result.Data!.Violations);
            return;
        }

        var violation = Assert.Single(result.Data!.Violations);
        Assert.Equal("Roles/Reader/Sales", violation.ObjectPath);
        Assert.Equal(ModelObjectKind.TablePermission, violation.ObjectKind);
        Assert.Equal("Reader/Sales", violation.ObjectName);
    }

    [Fact]
    public void ApplyFixes_WithoutSnapshot_ReportsFixError()
    {
        var rules = new List<BpaRule>
        {
            new("Q", "q", "DAX Expressions", RuleSeverity.Warning, ["Measure"], FixExpression: "QualifyColumnReferences()")
        };
        var violations = new List<BpaViolation>
        {
            new("Q", "q", "DAX Expressions", RuleSeverity.Warning, "Measure", "[M]", "Sales/M",
                CanFix: true, ObjectKind: ModelObjectKind.Measure)
        };

        var result = new BpaFixer().ApplyFixes(new MutationStubs.SnapshotSession(MutationStubs.BaseAndDerived()), violations, rules);

        Assert.Equal(0, result.FixesApplied);
        Assert.Contains("need the model snapshot", Assert.Single(result.Errors).Reason);
    }

    private static BpaDaxNames Names()
        => BpaDaxNames.FromSnapshot(new TmdlModelProvider()
            .OpenAsync(new ModelReference(SampleModel.Locate()), CancellationToken.None).Result
            .GetSnapshotAsync(CancellationToken.None).Result);

    private static string ModelWithMeasure(TempDir root, string name, string dax)
    {
        var model = SampleModel.CopyTo(root, "model");
        var sales = Path.Combine(model, "tables", "Sales.tmdl");
        var text = File.ReadAllText(sales).Replace(
            "\tcolumn SaleID",
            $"\tmeasure {name} = {dax}\n\n\tcolumn SaleID",
            StringComparison.Ordinal);
        File.WriteAllText(sales, text);
        return model;
    }

    private static string ModelWithRole(TempDir root, string filter)
    {
        var model = SampleModel.CopyTo(root, "model");
        var roles = Directory.CreateDirectory(Path.Combine(model, "roles"));
        File.WriteAllText(
            Path.Combine(roles.FullName, "Reader.tmdl"),
            $"role Reader\n\tmodelPermission: read\n\n\ttablePermission Sales = {filter}\n");
        return model;
    }

    private static async Task<string?> FilterAsync(string model)
    {
        await using var session = await new TmdlModelProvider().OpenAsync(new ModelReference(model), CancellationToken.None);
        var snapshot = await session.GetSnapshotAsync(CancellationToken.None);
        return snapshot.Objects.Single(o => o.Kind == ModelObjectKind.Role && o.Name == "Reader").Children
            .Single(c => c.Kind == ModelObjectKind.TablePermission).Expression;
    }

    private static async Task<string?> ExpressionAsync(string model, string measure)
    {
        await using var session = await new TmdlModelProvider().OpenAsync(new ModelReference(model), CancellationToken.None);
        var snapshot = await session.GetSnapshotAsync(CancellationToken.None);
        return snapshot.Objects.Single(o => o.Name == "Sales").Children
            .Single(c => c.Kind == ModelObjectKind.Measure && c.Name == measure).Expression;
    }

    private static Task<TomixResult<BpaRunResult>> RunAsync(
        TempConfigDir config, string model, string? ruleId, bool save, string? ruleset = null)
        => new BpaRunHandler([new TmdlModelProvider()], config.Stores, new BpaUserRuleState(config.Path), config.Path)
            .HandleAsync(
                new BpaRunRequest(new ModelReference(model))
                {
                    RuleIds = ruleId is null ? ["DAX_COLUMNS_FULLY_QUALIFIED", "DAX_MEASURES_UNQUALIFIED"] : [ruleId],
                    Fix = true,
                    Save = save,
                    NoModelRules = true,
                    Ruleset = ruleset,
                },
                CancellationToken.None);
}
