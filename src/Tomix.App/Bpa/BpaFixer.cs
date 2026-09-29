using System.Text.RegularExpressions;
using Tomix.App.ModelObjects;
using Tomix.App.Mutations;
using Tomix.Core.Bpa;
using Tomix.Core.Models;
using Tomix.Core.Properties;

namespace Tomix.App.Bpa;

public sealed partial class BpaFixer
{
    public BpaFixResult ApplyFixes(
        IModelMutationSession session,
        IReadOnlyList<BpaViolation> violations,
        IReadOnlyList<BpaRule> rules,
        bool allowDelete = false)
    {
        var ruleMap = rules.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var applied = 0;
        var skipped = 0;
        var destructiveSkipped = 0;
        var errors = new List<BpaFixError>();
        var changes = new List<BpaFixChange>();

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
                // Delete() removes model objects and reference tracking cannot see report-layer
                // or external consumers, so destructive fixes are opt-in.
                if (!allowDelete)
                {
                    destructiveSkipped++;
                    continue;
                }

                ApplyDelete(session, violation, ref applied, ref skipped, errors, changes);
                continue;
            }

            if (TryParseSimpleAssignment(fixExpr, out var assignments))
            {
                ApplySetProperty(session, violation, assignments, ref applied, ref skipped, errors, changes);
            }
            else
            {
                skipped++;
                errors.Add(new BpaFixError(violation.RuleId, violation.ObjectPath, "Unsupported fix expression"));
            }
        }

        return new BpaFixResult(applied, skipped, destructiveSkipped, errors, changes);
    }

    private static void ApplyDelete(
        IModelMutationSession session,
        BpaViolation violation,
        ref int applied,
        ref int skipped,
        List<BpaFixError> errors,
        List<BpaFixChange> changes)
    {
        try
        {
            var result = session.RemoveObject(new ModelObjectRemoveRequest(
                violation.ObjectPath,
                violation.ObjectKind,
                IfExists: false));

            if (result.Changed)
            {
                applied++;
                changes.Add(new BpaFixChange(
                    violation.RuleId, violation.ObjectType, violation.ObjectPath, BpaFixAction.Delete));
            }
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
        List<BpaFixError> errors,
        List<BpaFixChange> changes)
    {
        try
        {
            var result = session.SetProperty(new ModelObjectSetRequest(
                violation.ObjectPath,
                assignments,
                violation.ObjectKind));

            if (result.Changed)
            {
                applied++;
                var assignment = assignments[^1];
                changes.Add(new BpaFixChange(
                    violation.RuleId, violation.ObjectType, violation.ObjectPath, BpaFixAction.Set,
                    result.Property ?? assignment.Property, After: result.Value ?? assignment.Value)
                { ObjectKind = violation.ObjectKind });
            }
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

    /// <summary>
    /// Fills each property change's <see cref="BpaFixChange.Before"/> from the pre-fix snapshot,
    /// read through the property catalog (so <c>IsHidden</c> reads the same value <c>get</c>
    /// shows) with the raw property bag as fallback. Render-only: an object or property the
    /// snapshot does not carry leaves <c>Before</c> null.
    /// </summary>
    public static IReadOnlyList<BpaFixChange> WithBefore(IReadOnlyList<BpaFixChange> changes, ModelSnapshot before)
        => changes
            .Select(c => c.Action == BpaFixAction.Set && c.Property is not null
                ? c with { Before = ReadValue(before, c.ObjectPath, c.ObjectKind, c.Property) }
                : c)
            .ToList();

    private static string? ReadValue(ModelSnapshot snapshot, string path, ModelObjectKind? kind, string property)
    {
        var target = ModelObjectLookup.Find(snapshot, DaxObjectForm.Normalize(path), kind).FirstOrDefault();
        if (target is null)
            return null;

        var descriptor = ModelPropertyCatalog.For(target.Kind)
            .FirstOrDefault(d => string.Equals(d.JsonKey, property, StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.Header, property, StringComparison.OrdinalIgnoreCase));
        var value = descriptor is not null
            ? descriptor.Value(target)
            : target.Properties?.FirstOrDefault(p => string.Equals(p.Key, property, StringComparison.OrdinalIgnoreCase)).Value;

        return value switch
        {
            null => null,
            bool b => b ? "true" : "false",
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    [GeneratedRegex(@"^(?<prop>\w+)\s*=\s*(?<value>.+)$", RegexOptions.Compiled)]
    private static partial Regex FixAssignmentRegex();
}

public sealed record BpaFixResult(
    int FixesApplied,
    int FixesSkipped,
    int DestructiveFixesSkipped,
    IReadOnlyList<BpaFixError> Errors,
    IReadOnlyList<BpaFixChange> Changes);

public enum BpaFixAction { Set, Delete }

/// <summary>
/// One fix the fixer applied (or, under <c>--dry-run</c>, would apply): a property set with its
/// before/after values, or a <c>Delete()</c>.
/// </summary>
public sealed record BpaFixChange(
    string RuleId,
    string ObjectType,
    string ObjectPath,
    BpaFixAction Action,
    string? Property = null,
    string? Before = null,
    string? After = null)
{
    public ModelObjectKind? ObjectKind { get; init; }
}

public sealed record BpaFixError(
    string RuleId,
    string ObjectPath,
    string Reason);
