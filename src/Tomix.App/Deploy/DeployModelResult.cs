using System.Text.Json.Serialization;

using Tomix.App.Diff;

namespace Tomix.App.Deploy;

public sealed record DeployModelResult(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Server,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Database,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Status,
    long? DurationMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ScriptPath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Script,
    DiffModelResult? Diff = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DiffError = null,
    /// <summary>True on a dry run whose target database does not exist yet: the deploy creates
    /// it with the full source model, so there is no diff to show. Null everywhere else, and
    /// omitted from JSON so existing output is unchanged.</summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? CreatesDatabase = null);
