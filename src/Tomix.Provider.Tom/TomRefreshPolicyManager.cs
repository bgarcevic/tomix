using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;

namespace Tomix.Provider.Tom;

/// <summary>
/// Reads, writes, and validates a table's incremental refresh policy
/// (<see cref="BasicRefreshPolicy"/>). Read (show) and write (set) share <see cref="Validate"/>
/// so both paths report identical issues. Validation issue codes are result payload
/// (lowercase snake tokens), not TOMIX_ diagnostic codes.
/// </summary>
public sealed class TomRefreshPolicyManager
{
    private const string RangeStart = "RangeStart";
    private const string RangeEnd = "RangeEnd";

    // The Power BI incremental-refresh parameter convention: the literal value is a placeholder;
    // the service overrides it per generated partition. Names and the DateTime meta are load-bearing.
    private const string RangeParameterExpression =
        "#datetime(2024, 1, 1, 0, 0, 0) meta [IsParameterQuery=true, Type=\"DateTime\", IsParameterQueryRequired=true]";

    private const int MinimumCompatibilityLevel = 1450;
    private const int HybridCompatibilityLevel = 1565;

    private readonly Database _database;

    public TomRefreshPolicyManager(Database database) => _database = database;

    public RefreshPolicyInfo? Get(string tableName)
    {
        var table = RequireTable(tableName);
        return table.RefreshPolicy is BasicRefreshPolicy ? BuildInfo(table) : null;
    }

    public RefreshPolicySetResult Set(RefreshPolicySetRequest request)
    {
        var table = RequireTable(request.Table);
        var created = table.RefreshPolicy is not BasicRefreshPolicy;

        if (created)
            RequireCreateFields(request);

        var existing = table.RefreshPolicy as BasicRefreshPolicy;

        // Compatibility-level constraints are enforced by TOM itself: assigning an incompatible
        // policy (or setting Hybrid on a low-compat model) throws before we could report our own
        // findings, and --force cannot bypass them. Pre-flight them so the caller gets a clean
        // RefreshPolicyValidationException instead of a raw TOM exception.
        PreflightCompatibility(request, existing);

        var policy = existing ?? new BasicRefreshPolicy();

        if (request.Mode is not null)
            policy.Mode = ParseMode(request.Mode);
        if (request.RollingWindowGranularity is not null)
            policy.RollingWindowGranularity = ParseGranularity(request.RollingWindowGranularity, "--rolling-window-granularity");
        if (request.RollingWindowPeriods is not null)
            policy.RollingWindowPeriods = request.RollingWindowPeriods.Value;
        if (request.IncrementalGranularity is not null)
            policy.IncrementalGranularity = ParseGranularity(request.IncrementalGranularity, "--incremental-granularity");
        if (request.IncrementalPeriods is not null)
            policy.IncrementalPeriods = request.IncrementalPeriods.Value;
        if (request.IncrementalOffset is not null)
            policy.IncrementalPeriodsOffset = request.IncrementalOffset.Value;
        if (request.PollingExpression is not null)
            policy.PollingExpression = string.IsNullOrWhiteSpace(request.PollingExpression) ? null : request.PollingExpression;
        if (request.SourceExpression is not null)
            policy.SourceExpression = request.SourceExpression;

        if (created)
            table.RefreshPolicy = policy;

        var createdExpressions = created ? EnsureRangeParameters() : Array.Empty<string>();

        var issues = Validate(table);
        if (!request.Force && issues.Any(i => i.IsError))
        {
            var errors = issues.Where(i => i.IsError).Select(i => i.Message);
            throw new RefreshPolicyValidationException(
                $"Refresh policy for '{table.Name}' has validation errors: {string.Join(" ", errors)}",
                issues);
        }

        return new RefreshPolicySetResult(BuildInfo(table), created, createdExpressions);
    }

    public ModelObjectMutationResult Remove(string tableName, bool ifExists)
    {
        var table = RequireTable(tableName);

        if (table.RefreshPolicy is null)
        {
            if (ifExists)
                return new ModelObjectMutationResult(table.Name, Changed: false, Reason: "not_found");

            throw new RefreshPolicyNotFoundException(
                $"Table '{table.Name}' has no incremental refresh policy.");
        }

        table.RefreshPolicy = null;
        return new ModelObjectMutationResult(table.Name, Changed: true);
    }

    private void PreflightCompatibility(RefreshPolicySetRequest request, BasicRefreshPolicy? existing)
    {
        var issues = new List<RefreshPolicyIssue>();

        if (_database.CompatibilityLevel < MinimumCompatibilityLevel)
            issues.Add(Error("compat_level",
                $"Refresh policies require compatibility level {MinimumCompatibilityLevel}+ (model is {_database.CompatibilityLevel})."));

        var effectiveMode = request.Mode is not null
            ? ParseMode(request.Mode)
            : existing?.Mode ?? RefreshPolicyMode.Import;
        if (effectiveMode == RefreshPolicyMode.Hybrid && _database.CompatibilityLevel < HybridCompatibilityLevel)
            issues.Add(Error("hybrid_compat_level",
                $"Hybrid mode requires compatibility level {HybridCompatibilityLevel}+ (model is {_database.CompatibilityLevel})."));

        if (issues.Count > 0)
            throw new RefreshPolicyValidationException(
                $"Refresh policy for '{request.Table}' is incompatible with the model: {string.Join(" ", issues.Select(i => i.Message))}",
                issues);
    }

    private void RequireCreateFields(RefreshPolicySetRequest request)
    {
        var missing = new List<string>();
        if (request.RollingWindowPeriods is null)
            missing.Add("--rolling-window-periods");
        if (request.RollingWindowGranularity is null)
            missing.Add("--rolling-window-granularity");
        if (request.IncrementalPeriods is null)
            missing.Add("--incremental-periods");
        if (request.IncrementalGranularity is null)
            missing.Add("--incremental-granularity");
        if (string.IsNullOrWhiteSpace(request.SourceExpression))
            missing.Add("--source-expression");

        if (missing.Count > 0)
            throw new ArgumentException(
                $"Creating a refresh policy requires {string.Join(", ", missing)}.");
    }

    private IReadOnlyList<string> EnsureRangeParameters()
    {
        var created = new List<string>();
        foreach (var name in new[] { RangeStart, RangeEnd })
        {
            // Exact-case check: the service resolves these reserved parameter names case-sensitively.
            if (_database.Model.Expressions.Any(e => string.Equals(e.Name, name, StringComparison.Ordinal)))
                continue;

            _database.Model.Expressions.Add(new NamedExpression
            {
                Name = name,
                Kind = ExpressionKind.M,
                Expression = RangeParameterExpression
            });
            created.Add(name);
        }

        return created;
    }

    private List<RefreshPolicyIssue> Validate(Table table)
    {
        var issues = new List<RefreshPolicyIssue>();
        if (table.RefreshPolicy is not BasicRefreshPolicy policy)
            return issues;

        if (_database.CompatibilityLevel < MinimumCompatibilityLevel)
            issues.Add(Error("compat_level",
                $"Refresh policies require compatibility level {MinimumCompatibilityLevel}+ (model is {_database.CompatibilityLevel})."));

        if (policy.Mode == RefreshPolicyMode.Hybrid && _database.CompatibilityLevel < HybridCompatibilityLevel)
            issues.Add(Error("hybrid_compat_level",
                $"Hybrid mode requires compatibility level {HybridCompatibilityLevel}+ (model is {_database.CompatibilityLevel})."));

        foreach (var name in new[] { RangeStart, RangeEnd })
            ValidateRangeParameter(name, issues);

        if (string.IsNullOrWhiteSpace(policy.SourceExpression))
        {
            issues.Add(Error("source_expression_missing",
                "The policy has no source expression. Provide the M query that filters on RangeStart/RangeEnd."));
        }
        else if (!policy.SourceExpression.Contains(RangeStart, StringComparison.Ordinal)
            || !policy.SourceExpression.Contains(RangeEnd, StringComparison.Ordinal))
        {
            issues.Add(Error("source_expression_range_refs",
                "The source expression must reference both RangeStart and RangeEnd (filter with >= RangeStart and < RangeEnd)."));
        }

        if (policy.RollingWindowPeriods < 1 || policy.IncrementalPeriods < 1)
            issues.Add(Error("periods_not_positive",
                "Rolling window and incremental periods must be at least 1."));

        if (GranularityRank(policy.IncrementalGranularity) > GranularityRank(policy.RollingWindowGranularity))
            issues.Add(Error("granularity_order",
                $"Incremental granularity ({policy.IncrementalGranularity}) must be the same as or finer than the rolling window granularity ({policy.RollingWindowGranularity})."));

        if (string.IsNullOrWhiteSpace(policy.PollingExpression))
            issues.Add(new RefreshPolicyIssue("no_polling_expression", RefreshPolicyIssue.SeverityWarning,
                "No polling expression set; detect-data-changes is off and every partition in the incremental window refreshes fully."));

        return issues;
    }

    private void ValidateRangeParameter(string name, List<RefreshPolicyIssue> issues)
    {
        var expression = _database.Model.Expressions
            .FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));

        if (expression is null)
        {
            issues.Add(Error("range_parameter_missing",
                $"Shared expression '{name}' is missing. Incremental refresh requires the reserved DateTime parameters RangeStart and RangeEnd."));
            return;
        }

        if (expression.Kind != ExpressionKind.M)
        {
            issues.Add(Error("range_parameter_kind",
                $"Shared expression '{name}' must be an M expression (found {expression.Kind})."));
            return;
        }

        var text = expression.Expression ?? "";
        if (!text.Contains("IsParameterQuery=true", StringComparison.OrdinalIgnoreCase)
            || !text.Contains("Type=\"DateTime\"", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(Error("range_parameter_meta",
                $"Shared expression '{name}' must be a DateTime parameter: meta [IsParameterQuery=true, Type=\"DateTime\", ...]."));
        }
    }

    private RefreshPolicyInfo BuildInfo(Table table)
    {
        var policy = (BasicRefreshPolicy)table.RefreshPolicy;
        return new RefreshPolicyInfo(
            table.Name,
            policy.Mode.ToString(),
            policy.RollingWindowGranularity.ToString(),
            policy.RollingWindowPeriods,
            policy.IncrementalGranularity.ToString(),
            policy.IncrementalPeriods,
            policy.IncrementalPeriodsOffset,
            policy.PollingExpression ?? "",
            policy.SourceExpression ?? "",
            PolicyPartitionNames(table),
            Validate(table));
    }

    /// <summary>
    /// One-line summary for the table property bag ("" when the table has no policy), e.g.
    /// "Import: keep 10 Year, refresh 3 Day, detect changes".
    /// </summary>
    public static string Summarize(Table table)
    {
        if (table.RefreshPolicy is not BasicRefreshPolicy policy)
            return "";

        var summary = $"{policy.Mode}: keep {policy.RollingWindowPeriods} {policy.RollingWindowGranularity}, " +
            $"refresh {policy.IncrementalPeriods} {policy.IncrementalGranularity}";
        if (policy.IncrementalPeriodsOffset != 0)
            summary += $", offset {policy.IncrementalPeriodsOffset}";
        if (!string.IsNullOrWhiteSpace(policy.PollingExpression))
            summary += ", detect changes";
        return summary;
    }

    private static IReadOnlyList<string> PolicyPartitionNames(Table table)
        => table.Partitions
            .Where(p => p.SourceType == PartitionSourceType.PolicyRange)
            .Select(p => p.Name)
            .ToList();

    private Table RequireTable(string name)
        => _database.Model.Tables.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ObjectNotFoundException(
                $"Table not found: {name}",
                hint: "Run 'tx ls tables' to list tables.");

    private static RefreshPolicyMode ParseMode(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "import" => RefreshPolicyMode.Import,
            "hybrid" => RefreshPolicyMode.Hybrid,
            _ => throw new ArgumentException($"Unknown refresh policy mode: '{value}'. Known values: import, hybrid.")
        };

    private static RefreshGranularityType ParseGranularity(string value, string option)
    {
        if (Enum.TryParse<RefreshGranularityType>(value.Trim(), ignoreCase: true, out var parsed)
            && parsed != RefreshGranularityType.Invalid)
            return parsed;

        throw new ArgumentException($"Unknown granularity for {option}: '{value}'. Known values: day, month, quarter, year.");
    }

    private static int GranularityRank(RefreshGranularityType granularity)
        => granularity switch
        {
            RefreshGranularityType.Day => 0,
            RefreshGranularityType.Month => 1,
            RefreshGranularityType.Quarter => 2,
            RefreshGranularityType.Year => 3,
            _ => -1
        };

    private static RefreshPolicyIssue Error(string code, string message)
        => new(code, RefreshPolicyIssue.SeverityError, message);
}
