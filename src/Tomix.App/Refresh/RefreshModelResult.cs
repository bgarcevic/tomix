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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Script);
