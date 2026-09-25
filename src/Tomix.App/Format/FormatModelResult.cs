using System.Text.Json.Serialization;
using Tomix.App.Mutations;

namespace Tomix.App.Format;

/// <summary>Marker for the three <c>tx format</c> result shapes.</summary>
public interface IFormatModelResult;

public sealed record InlineFormatResult(
    bool Success,
    string Formatted,
    string Language,
    IReadOnlyList<string> Errors) : IFormatModelResult;

public sealed record ObjectFormatResult(
    bool Success,
    string Path,
    string Language,
    string FormatStatus,
    string Formatted) : MutationResult, IFormatModelResult;

public sealed record ModelFormatResult(
    int Total,
    int Formatted,
    int Unchanged,
    int Failed,
    IReadOnlyList<ModelFormatObjectResult> Results) : MutationResult, IFormatModelResult;

public sealed record ModelFormatObjectResult(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Measure,
    string Table,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Partition,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Error = null);
