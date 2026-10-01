using System.Text.RegularExpressions;
using Tomix.App.Bpa;
using Tomix.App.Validate;
using Tomix.Core.Bpa;
using Tomix.Core.Rules;
using Tomix.Tests.Support;

namespace Tomix.App.Tests;

/// <summary>
/// Pins the shared rule shape (#230): <c>validate</c> and <c>bpa</c> describe their rules with one
/// <see cref="RuleDefinition"/> and one <see cref="RuleSeverity"/>, and <c>validate</c> raises
/// issues only through its rule catalog, which must agree with <c>docs/error-codes.md</c>.
/// </summary>
public sealed class RuleDefinitionTests
{
    private static readonly Regex ValidateCodeRow = new(@"^\| `([A-Z0-9_]+)` \| (Error|Warning|Info) \|", RegexOptions.Compiled);

    [Theory]
    [InlineData(RuleSeverity.Info, 1)]
    [InlineData(RuleSeverity.Warning, 2)]
    [InlineData(RuleSeverity.Error, 3)]
    public void Severity_KeepsTheValuesRuleFilesAndBpaJsonUse(RuleSeverity severity, int value)
        => Assert.Equal(value, (int)severity);

    [Fact]
    public void BpaRule_IsARuleDefinition()
    {
        RuleDefinition rule = new BpaRule(
            "R", "Rule", "Performance", RuleSeverity.Warning, ["Measure"],
            Description: "d", Expression: "IsHidden", FixExpression: "IsHidden = false", CompatibilityLevel: 1500);

        Assert.Equal(("R", "Rule", "Performance", RuleSeverity.Warning), (rule.Id, rule.Name, rule.Category, rule.Severity));
        Assert.Equal(["Measure"], rule.Scope);
        Assert.Equal(("d", "IsHidden", "IsHidden = false"), (rule.Description, rule.Expression, rule.FixExpression));
        Assert.Null(rule.DocsUrl);
    }

    [Fact]
    public void BundledBpaCatalog_FillsTheSharedShape()
    {
        var rules = BpaRuleLoader.LoadBundledCatalog();

        Assert.NotEmpty(rules);
        Assert.All(rules, rule =>
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Id));
            Assert.False(string.IsNullOrWhiteSpace(rule.Category));
            Assert.NotEmpty(rule.Scope);
            Assert.True(Enum.IsDefined(rule.Severity), $"{rule.Id} has severity {(int)rule.Severity}");
        });
    }

    [Fact]
    public void ValidationRules_HaveUniqueIdsAndTheSharedShape()
    {
        Assert.Equal(
            ValidationRules.All.Count,
            ValidationRules.All.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count());

        Assert.All(ValidationRules.All, rule =>
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Name));
            Assert.False(string.IsNullOrWhiteSpace(rule.Category));
            Assert.NotEmpty(rule.Scope);
            Assert.Null(rule.Expression);
            Assert.Equal(ValidationRules.DocsUrl, rule.DocsUrl);
        });
    }

    [Fact]
    public void ValidationRule_Issue_CarriesTheRulesCodeAndSeverity()
    {
        var issue = ValidationRules.UnresolvedReference.Issue("message", "Sales/Total", "3", "  [X]");

        Assert.Equal(new ValidationIssue(RuleSeverity.Warning, "DAX0003", "message", "Sales/Total", "3", "  [X]"), issue);
    }

    /// <summary>
    /// The catalog and the documented validate issue table list the same codes with the same
    /// severities, so a new rule cannot ship undocumented and the docs cannot go stale.
    /// </summary>
    [Fact]
    public void ValidationRules_MatchTheDocumentedIssueCodes()
    {
        var documented = DocumentedValidateCodes();
        Assert.NotEmpty(documented); // guards against the docs table being renamed away

        var catalog = ValidationRules.All.ToDictionary(rule => rule.Id, rule => rule.Severity.ToString());

        Assert.Equal(documented.OrderBy(p => p.Key), catalog.OrderBy(p => p.Key));
    }

    /// <summary>Validate issues are raised only through a rule, never constructed ad hoc.</summary>
    [Fact]
    public void ValidationIssues_AreOnlyConstructedByValidationRule()
    {
        var offenders = Directory
            .EnumerateFiles(RepoPaths.Combine("src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(file => Path.GetFileName(file) != "ValidationRule.cs")
            .Where(file => File.ReadAllText(file).Contains("new ValidationIssue(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, $"Raise validate issues through ValidationRules: {string.Join(", ", offenders)}");
    }

    private static Dictionary<string, string> DocumentedValidateCodes()
    {
        var codes = new Dictionary<string, string>(StringComparer.Ordinal);
        var inSection = false;

        foreach (var line in File.ReadLines(RepoPaths.Combine("docs", "error-codes.md")))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inSection)
                    break;
                inSection = line == "## Validate Issue Codes";
                continue;
            }

            if (inSection && ValidateCodeRow.Match(line) is { Success: true } match)
                codes[match.Groups[1].Value] = match.Groups[2].Value;
        }

        return codes;
    }
}
