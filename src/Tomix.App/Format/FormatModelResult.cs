using System.Text.Json.Serialization;

namespace Tomix.App.Format;

public abstract record FormatModelResult;

public sealed record InlineFormatResult(
    bool Success,
    string Formatted,
    IReadOnlyList<string> Errors) : FormatModelResult;

public sealed record ObjectFormatResult(
    bool Success,
    string Path,
    string Language,
    string Status,
    string Formatted,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    object? Saved,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Staged = null,
    bool Synced = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SyncTarget = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SyncWarning = null) : FormatModelResult;

public sealed record ModelFormatResult(
    int Total,
    int Formatted,
    int Unchanged,
    int Failed,
    IReadOnlyList<ModelFormatObjectResult> Results,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    object? Saved,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Staged = null,
    bool Synced = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SyncTarget = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SyncWarning = null) : FormatModelResult;

public sealed record ModelFormatObjectResult(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Measure,
    string Table,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Partition);
