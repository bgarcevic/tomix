using System.Text.RegularExpressions;
using Tomix.Core.Bpa;
using Tomix.Core.Models;

namespace Tomix.App.Bpa;

public sealed partial class BpaFixer
{
    public BpaFixResult ApplyFixes(
        IModelMutationSession session,
        IReadOnlyList<BpaViolation> violations,
        IReadOnlyList<BpaRule> rules)
    {
        var ruleMap = rules.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var applied = 0;
        var skipped = 0;
        var errors = new List<BpaFixError>();

        foreach (var violation in violations.Where(v => v.CanFix))
        {
            if (!ruleMap.TryGetValue(violation.RuleId, out var rule) ||
                string.IsNullOrWhiteSpace(rule.FixExpression))
            {
                skipped++;
                continue;
            }

            var fixExpr = rule.FixExpression.Trim();

            if (fixExpr.Equals("Delete()", StringComparison.OrdinalIgnoreCase))
            {
                ApplyDelete(session, violation, ref applied, ref skipped, errors);
                continue;
            }

            if (TryParseSimpleAssignment(fixExpr, out var assignments))
            {
                ApplySetProperty(session, violation, assignments, ref applied, ref skipped, errors);
            }
            else
            {
                skipped++;
                errors.Add(new BpaFixError(violation.RuleId, violation.ObjectPath, "Unsupported fix expression"));
            }
        }

        return new BpaFixResult(applied, skipped, errors);
    }

    private static void ApplyDelete(
        IModelMutationSession session,
        BpaViolation violation,
        ref int applied,
        ref int skipped,
        List<BpaFixError> errors)
    {
        try
        {
            var result = session.RemoveObject(new ModelObjectRemoveRequest(
                violation.ObjectPath,
                violation.ObjectKind,
                IfExists: false));

            if (result.Changed)
                applied++;
            else
                skipped++;
        }
        catch (Exception ex)
        {
            errors.Add(new BpaFixError(violation.RuleId, violation.ObjectPath, ex.Message));
        }
    }

    private static void ApplySetProperty(
        IModelMutationSession session,
        BpaViolation violation,
        IReadOnlyList<ModelPropertyAssignment> assignments,
        ref int applied,
        ref int skipped,
        List<BpaFixError> errors)
    {
        try
        {
            var result = session.SetProperty(new ModelObjectSetRequest(
                violation.ObjectPath,
                assignments,
                violation.ObjectKind));

            if (result.Changed)
                applied++;
            else
                skipped++;
        }
        catch (Exception ex)
        {
            errors.Add(new BpaFixError(violation.RuleId, violation.ObjectPath, ex.Message));
        }
    }

    public static bool TryParseSimpleAssignment(
        string fixExpression,
        out IReadOnlyList<ModelPropertyAssignment> assignments)
    {
        assignments = [];
        var match = FixAssignmentRegex().Match(fixExpression);
        if (!match.Success)
            return false;

        var prop = match.Groups["prop"].Value;
        var rawValue = match.Groups["value"].Value.Trim();

        if (rawValue.Contains('(') || rawValue.Contains('['))
            return false;

        var value = ParseValue(rawValue);

        assignments = [new ModelPropertyAssignment(prop, value)];
        return true;
    }

    private static string ParseValue(string rawValue)
    {
        if (rawValue.StartsWith('"') && rawValue.EndsWith('"') && rawValue.Length >= 2)
            return rawValue[1..^1];

        if (bool.TryParse(rawValue, out _))
            return rawValue.ToLowerInvariant();

        var dotIndex = rawValue.IndexOf('.');
        if (dotIndex > 0 && dotIndex < rawValue.Length - 1)
            return rawValue[(dotIndex + 1)..];

        return rawValue;
    }

    [GeneratedRegex(@"^(?<prop>\w+)\s*=\s*(?<value>.+)$", RegexOptions.Compiled)]
    private static partial Regex FixAssignmentRegex();
}

public sealed record BpaFixResult(
    int FixesApplied,
    int FixesSkipped,
    IReadOnlyList<BpaFixError> Errors);

public sealed record BpaFixError(
    string RuleId,
    string ObjectPath,
    string Reason);
