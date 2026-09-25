using System.Text.Json.Serialization;
using Tomix.Core.Models;

namespace Tomix.App.Refresh;

public sealed record RefreshModelResult(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Server,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Database,
    string RefreshType,
    long DurationMs,
    IReadOnlyList<RefreshTableResult> Tables,
    RefreshTableResult? Totals,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Script,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RefreshPolicyApplyResult? PolicyApplication = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PolicyOnlyPreview? PolicyPreview = null);

/// <summary>A validated, non-executing preview; partition changes are determined only by the server on apply.</summary>
public sealed record PolicyOnlyPreview(string Table, DateOnly EffectiveDate, int? MaxParallelism,
    bool LoadsData = false, string Operation = "applyRefreshPolicy");
