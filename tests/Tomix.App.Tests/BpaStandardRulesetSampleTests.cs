using Tomix.App.Bpa;
using Tomix.Core.Bpa;
using Tomix.Core.Models;
using Tomix.Core.Rules;
using Tomix.Provider.Tmdl;

namespace Tomix.App.Tests;

/// <summary>
/// Pins the <c>standard</c> ruleset against the real sample models. The deploy gate evaluates
/// this ruleset and blocks on error severity by default, so a working model must not raise an
/// error-severity finding. Each sample here once did, through a style rule set to error or a
/// false positive (calculation-group columns, field parameters, inferred column types).
/// </summary>
public sealed class BpaStandardRulesetSampleTests
{
    public static TheoryData<string> WorkingSamples =>
    [
        "basic-tmdl",
        "deploy-qa",
        "qa-fixture-variant",
        "AdventureWorks Sales.SemanticModel",
        "Competitive Marketing Analysis.SemanticModel",
        "Corporate Spend.SemanticModel",
        "Employee Hiring and History.SemanticModel",
        "Regional Sales Sample.SemanticModel",
        "Revenue Opportunities.SemanticModel",
        "Store Sales.SemanticModel",
    ];

    [Theory]
    [MemberData(nameof(WorkingSamples))]
    public async Task Standard_WorkingSample_HasNoErrorSeverityFindings(string sample)
    {
        var violations = await EvaluateStandardAsync(sample);

        var errors = violations
            .Where(v => v.Severity == RuleSeverity.Error)
            .Select(v => $"{v.RuleId} {v.ObjectPath}");
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Standard_UnavailableInMdxSortColumn_IsTheOnlyErrorInTheAiSample()
    {
        // 'Opportunity Calendar'[DAY] is used as a sort-by column but hidden from MDX: a real
        // defect (Excel cannot sort by it), and auto-fixable, so the gate should still see it.
        var violations = await EvaluateStandardAsync("Artificial Intelligence Sample.SemanticModel");

        var error = Assert.Single(violations, v => v.Severity == RuleSeverity.Error);
        Assert.Equal("SET_ISAVAILABLEINMDX_TO_TRUE_ON_NECESSARY_COLUMNS", error.RuleId);
    }

    [Fact]
    public async Task RelationshipColumnsSameDataType_SkipsInferredTypes_ButKeepsDeclaredMismatches()
    {
        // Opportunities[CampaignSeq] (string) -> Campaigns[CampaignSeq] (int64) is a declared
        // mismatch. 'Case Calendar'[Date] declares no dataType, so its type is inferred and the
        // relationship to Cases[Case Created On] must not be reported.
        var violations = await EvaluateStandardAsync("Artificial Intelligence Sample.SemanticModel");

        var flagged = violations
            .Where(v => v.RuleId == "RELATIONSHIP_COLUMNS_SAME_DATA_TYPE")
            .Select(v => v.ObjectName)
            .ToList();
        Assert.Contains(flagged, name => name.Contains("[CampaignSeq] -> Campaigns[CampaignSeq]"));
        Assert.DoesNotContain(flagged, name => name.Contains("Case Calendar"));
    }

    [Fact]
    public async Task DaxMeasuresUnqualified_IgnoresFieldParameterTables()
    {
        // 'Toggle for variance to plan' is a field parameter: NAMEOF('Calculations'[Var to Plan])
        // is the qualified form Power BI generates, not a style violation.
        var violations = await EvaluateStandardAsync("Corporate Spend.SemanticModel");

        Assert.DoesNotContain(violations, v => v.RuleId == "DAX_MEASURES_UNQUALIFIED");
    }

    [Fact]
    public async Task Standard_UnannotatedSample_NamesEveryStatisticsRuleAsNotChecked()
    {
        // #266: the sample has no Vertipaq_* annotations, so every bundled rule that reads them
        // must be reported as not checked rather than silently passing.
        var rules = await BpaRuleLoader.LoadDefaultRulesAsync(CancellationToken.None);
        await using var session = await new TmdlModelProvider()
            .OpenAsync(new ModelReference(SampleModel.Locate("basic-tmdl")), CancellationToken.None);
        var snapshot = await session.GetSnapshotAsync(CancellationToken.None);

        var result = new BpaEngine().Evaluate(snapshot, new BpaEngineOptions(rules));

        var expected = rules
            .Where(r => r.Expression?.Contains("GetAnnotation(\"Vertipaq_", StringComparison.Ordinal) == true)
            .Select(r => r.Id)
            .Order()
            .ToList();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, result.MissingVertipaqStatsRules.Select(r => r.RuleId).Order());
    }

    private static async Task<IReadOnlyList<BpaViolation>> EvaluateStandardAsync(string sample)
    {
        var rules = await BpaRuleLoader.LoadDefaultRulesAsync(CancellationToken.None);
        await using var session = await new TmdlModelProvider()
            .OpenAsync(new ModelReference(SampleModel.Locate(sample)), CancellationToken.None);
        var snapshot = await session.GetSnapshotAsync(CancellationToken.None);

        var result = new BpaEngine().Evaluate(snapshot, new BpaEngineOptions(rules));
        Assert.Empty(result.RuleErrorViolations);
        return result.Violations;
    }
}
