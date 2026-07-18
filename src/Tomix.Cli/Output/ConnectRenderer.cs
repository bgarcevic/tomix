using Spectre.Console;
using Tomix.App.Connect;
using Tomix.App.Info;
using Tomix.App.State;
using Tomix.Core.Models;

namespace Tomix.Cli.Output;

/// <summary>
/// Rendering and JSON projection for <c>connect</c>: the connected-model summary, the
/// show-current and raw-connection views, and the workspace error-message trim.
/// </summary>
internal static class ConnectRenderer
{
    public static void RenderConnectedModel(
        InfoModelResult result,
        string format,
        string? model,
        string? remoteServer,
        string? database,
        string? workspace)
    {
        if (OutputFormats.IsJson(format))
            JsonOutput.Write(ProjectConnectedModelJson(result, model, remoteServer, database, workspace));
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
        AnsiConsole.MarkupLine(model is not null
            ? Styling.Success($"Active: {Styling.MarkupEscape(Path.GetFullPath(model))}")
            : Styling.Success($"Active: {Styling.MarkupEscape(remoteServer ?? "")}{(string.IsNullOrWhiteSpace(database) ? "" : $" / {Styling.MarkupEscape(database)}")}"));
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
            return;
        }

        var connection = result.Connection;
        if (!string.IsNullOrWhiteSpace(connection.Model))
        {
            AnsiConsole.MarkupLine(Styling.Success("Active: local model"));
            AnsiConsole.MarkupLine(Styling.KeyValue("Path:", Styling.MarkupEscape(Path.GetFullPath(connection.Model))));
        }
        else
        {
            AnsiConsole.MarkupLine(Styling.Success(
                string.IsNullOrWhiteSpace(connection.Database)
                    ? $"Active: {Styling.MarkupEscape(connection.Server ?? "")}"
                    : $"Active: {Styling.MarkupEscape(connection.Server ?? "")} / {Styling.MarkupEscape(connection.Database)}"));
        }

        if (!string.IsNullOrWhiteSpace(connection.Workspace))
        {
            var mirrorDatabase = !string.IsNullOrWhiteSpace(connection.Database) ? connection.Database : null;
            AnsiConsole.MarkupLine(Styling.KeyValue("Mirror:",
                mirrorDatabase is not null
                    ? $"{connection.Workspace} / {mirrorDatabase}"
                    : connection.Workspace));
        }
    }

    public static void RenderConnection(CliConnectionState connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.Profile))
            AnsiConsole.MarkupLine(Styling.KeyValue("profile:", connection.Profile));
        if (!string.IsNullOrWhiteSpace(connection.Model))
            AnsiConsole.MarkupLine(Styling.KeyValue("model:", connection.Model));
        if (!string.IsNullOrWhiteSpace(connection.Server))
            AnsiConsole.MarkupLine(Styling.KeyValue("server:", connection.Server));
        if (!string.IsNullOrWhiteSpace(connection.Database))
            AnsiConsole.MarkupLine(Styling.KeyValue("database:", connection.Database));
        if (!string.IsNullOrWhiteSpace(connection.Workspace))
            AnsiConsole.MarkupLine(Styling.KeyValue("workspace:", connection.Workspace));
        if (!string.IsNullOrWhiteSpace(connection.WorkspaceFormat))
            AnsiConsole.MarkupLine(Styling.KeyValue("workspace-format:", connection.WorkspaceFormat));
        if (!string.IsNullOrWhiteSpace(connection.WorkspaceAuth))
            AnsiConsole.MarkupLine(Styling.KeyValue("workspace-auth:", connection.WorkspaceAuth));
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
