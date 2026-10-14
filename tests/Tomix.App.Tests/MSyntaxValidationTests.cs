using Tomix.App.Format.M;
using Tomix.App.Validate;
using Tomix.Core.Models;
using Tomix.Core.Results;
using Tomix.Core.Rules;
using Tomix.Provider.Tmdl;

namespace Tomix.App.Tests;

/// <summary>
/// The <c>validate</c> M syntax check (<c>M0001</c>): M partition sources and shared expressions
/// go through the offline engine; DAX partitions, <c>--no-m</c>, and engine failures do not
/// produce findings.
/// </summary>
public sealed class MSyntaxValidationTests
{
    public static TheoryData<string> TmdlSamples =>
    [
        "basic-tmdl",
        "deploy-qa",
        "qa-fixture",
        "qa-fixture-variant",
        "refresh-policy-qa",
        "AdventureWorks Sales.SemanticModel",
        "Artificial Intelligence Sample.SemanticModel",
        "Competitive Marketing Analysis.SemanticModel",
        "Corporate Spend.SemanticModel",
        "Employee Hiring and History.SemanticModel",
        "Regional Sales Sample.SemanticModel",
        "Revenue Opportunities.SemanticModel",
        "Store Sales.SemanticModel",
    ];

    [Fact]
    public async Task BrokenPartition_IsAWarningWithItsLine()
    {
        var result = await ValidateAsync(Snapshot(Partition("m", "let\n    Source = \"abc\nin\n    Source")));

        Assert.True(result.Data!.Valid);
        var issue = Assert.Single(result.Data.Warnings);
        Assert.Equal("M0001", issue.Code);
        Assert.Equal(RuleSeverity.Warning, issue.Severity);
        Assert.Equal("Sales/Part", issue.ObjectName);
        Assert.Equal("2", issue.Expression);
        Assert.Equal("    Source = \"abc", issue.ExpressionLine);
        Assert.Contains("column 14", issue.Message);
    }

    [Fact]
    public async Task BrokenSharedExpression_IsReported()
    {
        var expression = new ModelObject("Environment", ModelObjectKind.Expression, "Expressions/Environment",
            Detail: null, Expression: "let x = 1", Description: null, Hidden: false,
            SourceColumn: null, Children: []);

        var result = await ValidateAsync(Snapshot() with { Objects = [.. Snapshot().Objects, expression] });

        var issue = Assert.Single(result.Data!.Warnings);
        Assert.Equal("M0001", issue.Code);
        Assert.Equal("Expressions/Environment", issue.ObjectName);
    }

    [Fact]
    public async Task ValidPartition_HasNoFindings()
    {
        var result = await ValidateAsync(Snapshot(Partition("m", "let\n    Source = Sql.Database(\"s\", \"d\")\nin\n    Source")));

        Assert.Empty(result.Data!.Warnings);
        Assert.Empty(result.Data.Errors);
    }

    [Fact]
    public async Task CalculatedTablePartition_IsNotParsedAsM()
    {
        // Valid DAX, broken M: the partition carries DAX, so M0001 must not fire.
        var result = await ValidateAsync(Snapshot(Partition("calculated", "VAR x = 1 RETURN { x }")));

        Assert.DoesNotContain(result.Data!.Warnings, issue => issue.Code == "M0001");
    }

    [Fact]
    public async Task SkipM_SkipsTheCheck()
    {
        var result = await ValidateAsync(Snapshot(Partition("m", "let x = 1")), skipM: true);

        Assert.Empty(result.Data!.Warnings);
    }

    [Fact]
    public async Task EngineFailure_IsNotAFinding()
    {
        using var engine = new PowerQueryEngine(() => throw new InvalidOperationException("no bundle"));
        var handler = new ValidateModelHandler([new MutationStubs.Provider(new MutationStubs.SnapshotSession(Snapshot(Partition("m", "let x = 1"))))], engine);

        var result = await handler.HandleAsync(Request(skipM: false), CancellationToken.None);

        Assert.True(result.Data!.Valid);
        Assert.Empty(result.Data.Warnings);
    }

    [Theory]
    [MemberData(nameof(TmdlSamples))]
    public async Task Samples_HaveNoMSyntaxFindings(string sample)
    {
        var result = await new ValidateModelHandler([new TmdlModelProvider()]).HandleAsync(
            new ValidateModelRequest(new ModelReference(SampleModel.Locate(sample)), ErrorsOnly: false, NoWarnings: false, ServerOnly: false),
            CancellationToken.None);

        Assert.DoesNotContain(result.Data!.Warnings, issue => issue.Code == "M0001");
    }

    private static Task<TomixResult<ValidateModelResult>> ValidateAsync(ModelSnapshot snapshot, bool skipM = false)
        => new ValidateModelHandler([new MutationStubs.Provider(new MutationStubs.SnapshotSession(snapshot))]).HandleAsync(Request(skipM), CancellationToken.None);

    private static ValidateModelRequest Request(bool skipM)
        => new(new ModelReference("any"), ErrorsOnly: false, NoWarnings: false, ServerOnly: false, SkipM: skipM);

    private static ModelObject Partition(string detail, string expression)
        => new("Part", ModelObjectKind.Partition, "Sales/Part",
            Detail: detail, Expression: expression, Description: null, Hidden: false,
            SourceColumn: null, Children: []);

    private static ModelSnapshot Snapshot(params ModelObject[] partitions)
    {
        var sales = new ModelObject("Sales", ModelObjectKind.Table, "Sales",
            Detail: "regular", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: partitions);
        return new ModelSnapshot("test", 1601, [sales]);
    }

}
