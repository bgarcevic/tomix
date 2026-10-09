using Spectre.Console;
using Tomix.App.Connect;
using Tomix.App.Info;
using Tomix.App.State;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;

namespace Tomix.Cli.Output;

/// <summary>
/// Rendering and JSON projection for <c>connect</c>: the connected-model summary, the
/// show-current and raw-connection views, and the workspace error-message trim.
/// </summary>
internal static class ConnectRenderer
{
    /// <remarks>
    /// <paramref name="diagnostics"/> is threaded in rather than defaulted to empty because this
    /// renderer only receives the payload, not the enclosing <c>TomixResult</c>. Defaulting would
    /// make connect the one command that silently drops the envelope's diagnostics half — invisibly,
    /// since the key would still be present and still an empty array.
    /// </remarks>
    public static void RenderConnectedModel(
        InfoModelResult result,
        string format,
        string? model,
        string? remoteServer,
        string? database,
        string? workspace,
        IReadOnlyList<TomixDiagnostic> diagnostics)
    {
        if (OutputFormats.IsJson(format))
            JsonOutput.Write(new CommandEnvelope<object>(
                ProjectConnectedModelJson(result, model, remoteServer, database, workspace),
                diagnostics));
        else
            RenderConnectedModelText(result, model, remoteServer, database, workspace);
    }

    public static void RenderConnectedModelText(
        InfoModelResult result,
        string? model,
        string? remoteServer,
        string? database,
        string? workspace)
    {
        var s = result.Summary;
        var name = string.IsNullOrWhiteSpace(s.Name) ? "(unnamed)" : s.Name;
        AnsiConsole.MarkupLine(Styling.KeyValue("Model:", name));
        AnsiConsole.MarkupLine(Styling.KeyValue("  CL:", $"{s.CompatibilityLevel}"));
        AnsiConsole.MarkupLine(Styling.Muted($"  tables: {s.Tables}  measures: {s.Measures}  relationships: {s.Relationships}  roles: {s.Roles}"));
        AnsiConsole.WriteLine();
        var active = model is not null
            ? Path.GetFullPath(model)
            : string.IsNullOrWhiteSpace(database) ? remoteServer ?? "" : $"{remoteServer} / {database}";
        AnsiConsole.MarkupLine(Styling.Success($"Active: {active}"));
        if (!string.IsNullOrWhiteSpace(workspace))
            AnsiConsole.MarkupLine(Styling.KeyValue("Mirror:",
                !string.IsNullOrWhiteSpace(database)
                    ? $"{workspace} / {database}"
                    : workspace));
    }

    /// <summary>
    /// JSON projection for a validated connect. Property names are the output contract — keep stable.
    /// </summary>
    internal static object ProjectConnectedModelJson(
        InfoModelResult result,
        string? model,
        string? remoteServer,
        string? database,
        string? workspace)
    {
        var summary = result.Summary;
        var mirror = !string.IsNullOrWhiteSpace(workspace)
            ? new { workspace, database = string.IsNullOrWhiteSpace(database) ? (string?)null : database }
            : null;

        var shared = new
        {
            compatibilityLevel = summary.CompatibilityLevel,
            tables = summary.Tables,
            measures = summary.Measures,
            relationships = summary.Relationships,
            roles = summary.Roles
        };

        if (!string.IsNullOrWhiteSpace(model))
        {
            return new
            {
                kind = "local",
                path = Path.GetFullPath(model),
                mirror,
                shared.compatibilityLevel,
                shared.tables,
                shared.measures,
                shared.relationships,
                shared.roles
            };
        }

        return new
        {
            kind = ModelReference.IsLocalInstanceEndpoint(remoteServer) ? "local" : "remote",
            server = remoteServer,
            database = string.IsNullOrWhiteSpace(database) ? summary.DatabaseName : database,
            mirror,
            shared.compatibilityLevel,
            shared.tables,
            shared.measures,
            shared.relationships,
            shared.roles
        };
    }

    public static void RenderShow(ConnectShowResult result)
    {
        if (!result.Active || result.Connection is null)
        {
            AnsiConsole.MarkupLine(Styling.Warning("No active connection."));
            RenderSession(result.Session);
            return;
        }

        var connection = result.Connection;
        if (!string.IsNullOrWhiteSpace(connection.Model))
        {
            AnsiConsole.MarkupLine(Styling.Success("Active: local model"));
            AnsiConsole.MarkupLine(Styling.KeyValue("Path:", Path.GetFullPath(connection.Model)));
        }
        else
        {
            // For a Power BI Desktop session the port alone says nothing about which report is
            // open, so lead with the cached report name when it is still valid. ConnectHandler.Show
            // clears it otherwise, so reaching here means it does describe the live instance.
            var target = connection.ReportName is { } report
                ? $"{report}  ({connection.Server ?? ""})"
                : connection.Server ?? "";

            var active = string.IsNullOrWhiteSpace(connection.Database)
                ? $"Active: {target}"
                : $"Active: {target} / {connection.Database}";

            if (result.Reachable == false)
            {
                // The Desktop window this session pointed at has been closed; the port and GUID
                // alone would read as a healthy connection to something unidentifiable.
                AnsiConsole.MarkupLine(Styling.Warning($"{active}  (not running)"));
                AnsiConsole.MarkupLine(Styling.Guidance(result.LastReportName is { } last
                    ? $"Power BI Desktop is no longer serving '{last}' on {connection.Server}."
                    : $"Nothing is listening on {connection.Server}; the Power BI Desktop report it pointed at has been closed."));
                AnsiConsole.MarkupLine(Styling.Guidance(
                    "Run `tx connect --local` to pick an open report, or `tx connect --clear` to forget this one."));
            }
            else
            {
                AnsiConsole.MarkupLine(Styling.Success(active));
            }
        }

        if (!string.IsNullOrWhiteSpace(connection.Workspace))
        {
            var mirrorDatabase = !string.IsNullOrWhiteSpace(connection.Database) ? connection.Database : null;
            AnsiConsole.MarkupLine(Styling.KeyValue("Mirror:",
                mirrorDatabase is not null
                    ? $"{connection.Workspace} / {mirrorDatabase}"
                    : connection.Workspace));
        }

        RenderSession(result.Session);
    }

    /// <summary>
    /// Names the session file holding the connection, so it is clear why a different repository
    /// or worktree shows a different connection.
    /// </summary>
    private static void RenderSession(ConnectSessionInfo? session)
    {
        if (session is null)
            return;

        var label = session.Kind switch
        {
            "directory" when session.Scope is not null => session.Scope,
            "named" => $"{session.Id} (TOMIX_SESSION)",
            _ => session.Id,
        };
        AnsiConsole.MarkupLine(Styling.Muted($"Session: {label}"));
    }

    public static void RenderConnection(CliConnectionState connection)
    {
        (string Label, string? Value)[] rows =
        [
            ("profile:", connection.Profile),
            ("model:", connection.Model),
            ("server:", connection.Server),
            ("database:", connection.Database),
            ("workspace:", connection.Workspace),
            ("workspace-format:", connection.WorkspaceFormat),
            ("workspace-auth:", connection.WorkspaceAuth),
        ];
        Styling.WriteKeyValues([.. rows
            .Where(row => !string.IsNullOrWhiteSpace(row.Value))
            .Select(row => (row.Label, row.Value!))]);
    }

    /// <summary>
    /// Trims the provider's "Could not connect to '&lt;workspace&gt;': " prefix so the workspace
    /// error line doesn't repeat the workspace name the caller already prints.
    /// </summary>
    internal static string WorkspaceConnectMessage(string workspace, string message)
    {
        var prefix = $"Could not connect to '{workspace}': ";
        return message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? message[prefix.Length..]
            : message;
    }
}
