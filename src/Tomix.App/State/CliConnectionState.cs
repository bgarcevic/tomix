using System.Text.Json.Serialization;

namespace Tomix.App.State;

public sealed record CliConnectionState(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Server,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Database,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Model,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Auth,
    bool Local,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Profile,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Workspace = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? WorkspaceFormat = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? WorkspaceAuth = null,
    /// <summary>
    /// Cached Power BI Desktop report name for a <c>localhost:&lt;port&gt;</c> session, so showing
    /// the connection stays a file read instead of a ~220ms WMI query. Only trustworthy alongside
    /// <see cref="ReportPortFile"/>: <c>ConnectHandler.Show</c> drops it when that check fails, so
    /// a stale name is never displayed.
    /// </summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ReportName = null,
    /// <summary>
    /// The <c>msmdsrv.port.txt</c> that <see cref="ReportName"/> came from. Power BI creates a
    /// fresh workspace folder per session, so this file still holding <see cref="Server"/>'s port
    /// proves the cached name still belongs to the instance now on that port.
    /// </summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ReportPortFile = null)
{
    /// <summary>
    /// The same connection without the report-label cache. Those two fields are an internal
    /// display optimization, not part of the connection contract — and <see cref="ReportPortFile"/>
    /// is an absolute path inside the user's profile, which must not leak into command output or
    /// the recents file. Use this anywhere the state is serialized for someone other than the
    /// session file.
    /// </summary>
    public CliConnectionState WithoutReportCache()
        => ReportName is null && ReportPortFile is null
            ? this
            : this with { ReportName = null, ReportPortFile = null };
}

public sealed record CliProfile(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Server,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Database,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Model,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Auth,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Description,
    bool Local = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Workspace = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? WorkspaceFormat = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? WorkspaceAuth = null);
