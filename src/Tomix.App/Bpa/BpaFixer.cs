using System.Text.RegularExpressions;
using Tomix.App.Dax;
using Tomix.App.ModelObjects;
using Tomix.App.Mutations;
using Tomix.Core.Bpa;
using Tomix.Core.Models;
using Tomix.Core.Properties;

namespace Tomix.App.Bpa;

public sealed partial class BpaFixer
{
    /// <param name="snapshot">
    /// The model the violations were found in. DAX-rewrite fixes (<see cref="BpaDaxRewrite"/>)
    /// resolve references against it; without it they fail with a per-object fix error.
    /// </param>
    public BpaFixResult ApplyFixes(
        IModelMutationSession session,
        IReadOnlyList<BpaViolation> violations,
        IReadOnlyList<BpaRule> rules,
        bool allowDelete = false,
        ModelSnapshot? snapshot = null)
    {
        var ruleMap = rules.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var applied = 0;
        var skipped = 0;
        var destructiveSkipped = 0;
        var errors = new List<BpaFixError>();
        var changes = new List<BpaFixChange>();
        var daxRewrites = snapshot is null ? null : new DaxRewriteState(snapshot);

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

            if (BpaDaxRewriter.TryParse(fixExpr, out var rewrite))
            {
                ApplyDaxRewrite(session, daxRewrites, rewrite, violation, ref applied, ref skipped, errors, changes);
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

    /// <summary>
    /// Rewrites the violating object's DAX in place. A reference the rewrite cannot resolve with
    /// certainty fails that object with a fix error and leaves its expression untouched.
    /// </summary>
    private static void ApplyDaxRewrite(
        IModelMutationSession session,
        DaxRewriteState? state,
        BpaDaxRewrite rewrite,
        BpaViolation violation,
        ref int applied,
        ref int skipped,
        List<BpaFixError> errors,
        List<BpaFixChange> changes)
    {
        if (state is null)
        {
            errors.Add(new BpaFixError(violation.RuleId, violation.ObjectPath, "DAX rewrite fixes need the model snapshot"));
            return;
        }

        if (state.SiteFor(violation) is not { } site)
        {
            errors.Add(new BpaFixError(violation.RuleId, violation.ObjectPath, "Object has no DAX expression this fix can rewrite"));
            return;
        }

        var before = state.Current(site);
        var (after, error) = BpaDaxRewriter.Rewrite(before, rewrite, state.Names);
        if (error is not null)
        {
            errors.Add(new BpaFixError(violation.RuleId, violation.ObjectPath, error));
            return;
        }

        if (after == before)
        {
            skipped++;
            return;
        }

        if (session is not IExpressionRewriteSession rewriter)
        {
            errors.Add(new BpaFixError(violation.RuleId, violation.ObjectPath, "This provider does not support expression rewriting"));
            return;
        }

        try
        {
            rewriter.RewriteExpressions([new ModelExpressionEdit(site.Path, site.Kind, "Expression", after!)]);
        }
        catch (Exception ex)
        {
            errors.Add(new BpaFixError(violation.RuleId, violation.ObjectPath, ex.Message));
            return;
        }

        state.Update(site, after!);
        applied++;
        changes.Add(new BpaFixChange(
            violation.RuleId, violation.ObjectType, violation.ObjectPath, BpaFixAction.Set,
            "Expression", Before: before, After: after)
        { ObjectKind = violation.ObjectKind });
    }

    /// <summary>
    /// The DAX a rewrite fix edits for each violating object, tracking already-rewritten text so a
    /// second rewrite of the same object (one per qualification rule) builds on the first.
    /// </summary>
    private sealed class DaxRewriteState(ModelSnapshot snapshot)
    {
        private readonly Dictionary<(string Path, ModelObjectKind Kind), string> _rewritten = [];

        public BpaDaxNames Names { get; } = BpaDaxNames.FromSnapshot(snapshot);

        public DaxSite? SiteFor(BpaViolation violation)
        {
            var target = Flatten(snapshot.Objects).FirstOrDefault(o =>
                string.Equals(o.Path, violation.ObjectPath, StringComparison.Ordinal)
                && (violation.ObjectKind is null || o.Kind == violation.ObjectKind));

            return target?.Kind switch
            {
                ModelObjectKind.Measure or ModelObjectKind.Column or ModelObjectKind.CalculatedColumn
                    or ModelObjectKind.CalculationItem when !string.IsNullOrWhiteSpace(target.Expression)
                    => new DaxSite(target.Path, target.Kind, target.Expression!),

                // A calculated table's DAX lives on its calculated partition.
                ModelObjectKind.Table => target.Children
                    .Where(c => c.Kind == ModelObjectKind.Partition && DaxExpressions.IsCalculated(c)
                        && !string.IsNullOrWhiteSpace(c.Expression))
                    .Select(c => (DaxSite?)new DaxSite(c.Path, c.Kind, c.Expression!))
                    .FirstOrDefault(),

                _ => null,
            };
        }

        public string Current(DaxSite site)
            => _rewritten.GetValueOrDefault((site.Path, site.Kind), site.Expression);

        public void Update(DaxSite site, string expression)
            => _rewritten[(site.Path, site.Kind)] = expression;

        private static IEnumerable<ModelObject> Flatten(IEnumerable<ModelObject> objects)
            => objects.SelectMany(o => Flatten(o.Children).Prepend(o));
    }

    private readonly record struct DaxSite(string Path, ModelObjectKind Kind, string Expression);

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
    /// Fills each property change's <see cref="BpaFixChange.Before"/> from the pre-fix snapshot
    /// (a DAX rewrite already carries the text it rewrote), read through the property catalog (so <c>IsHidden</c> reads the same value <c>get</c>
    /// shows) with the raw property bag as fallback. Render-only: an object or property the
    /// snapshot does not carry leaves <c>Before</c> null.
    /// </summary>
    public static IReadOnlyList<BpaFixChange> WithBefore(IReadOnlyList<BpaFixChange> changes, ModelSnapshot before)
        => changes
            .Select(c => c.Action == BpaFixAction.Set && c.Property is not null && c.Before is null
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
/// One fix the fixer applied (or, in a preview, would apply): a property set with its
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
