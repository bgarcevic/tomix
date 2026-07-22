using System.CommandLine;
using Spectre.Console;
using Tomix.App.Connect;
using Tomix.App.Info;
using Tomix.App.Profile;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Authentication;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

/// <summary>
/// Thin CLI wrapper for <c>tx connect</c>. Parses options into a <see cref="ConnectPlanRequest"/>,
/// loops <see cref="ConnectPlanHandler.Plan"/> — resolving each reported need with a prompt or
/// Desktop discovery — then runs the planned validation/probe/init handlers and renders via
/// <see cref="ConnectRenderer"/>. All decision logic lives in Tomix.App.
/// </summary>
internal sealed class ConnectCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly IWorkspaceCatalog _workspaceCatalog;
    private readonly Func<string?> _cachedUsername;
    private readonly CliStateStore _state;

    public ConnectCommand(
        IReadOnlyList<IModelProvider> providers,
        IWorkspaceCatalog workspaceCatalog,
        Func<string?> cachedUsername,
        CliStateStore state)
    {
        _providers = providers;
        _workspaceCatalog = workspaceCatalog;
        _cachedUsername = cachedUsername;
        _state = state;
    }

    public Command Build()
    {
        var serverArgument = new Argument<string>("server")
        {
            Description = "Workspace name, endpoint, local model path (TMDL/BIM), or omit with --local.",
            Arity = ArgumentArity.ZeroOrOne
        };
        var databaseArgument = new Argument<string>("database")
        {
            Description = "Semantic model name (omit on a TTY to pick from the workspace's models).",
            Arity = ArgumentArity.ZeroOrOne
        };
        var workspaceOption = new Option<string?>("--workspace")
        {
            Description = "Enable workspace mode: mirror saves between the primary source and a secondary target. Primary remote -> pass a local folder or .bim file. Primary local -> pass <server> <database>. Pass -w with no value to pick the target workspace and model interactively.",
            Arity = ArgumentArity.ZeroOrOne
        };
        workspaceOption.Aliases.Add("-w");
        var localOption = new Option<bool>("--local")
        {
            Description = "Connect to a locally running Power BI Desktop instance (Windows only)"
        };
        var remoteOption = new Option<bool>("--remote")
        {
            Description = "Pick a workspace and semantic model interactively from your Power BI tenant (requires a TTY; sign in first with 'tx auth login')."
        };
        var profileOption = new Option<string?>("--profile")
        {
            Description = "Activate a saved connection profile (see: tx profile list)"
        };
        profileOption.Aliases.Add("-p");
        var clearOption = new Option<bool>("--clear")
        {
            Description = "Clear the active connection from CLI config"
        };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Overwrite a non-empty workspace target when initializing workspace mode."
        };
        var workspaceFormatOption = new Option<string?>("--workspace-format")
        {
            Description = "On-disk format for a local workspace (tmdl, bim, te-folder). Defaults to path-detected."
        };
        var workspaceAuthOption = new Option<string?>("--workspace-auth")
        {
            Description = "Auth method for a remote workspace (local primary). Defaults to --auth if set, else auto."
        };

        var command = new Command("connect", "Set active connection (workspace, local path, or PBI Desktop). No args = show current. --recent = reconnect to a recently used model.")
        {
            serverArgument,
            databaseArgument,
            workspaceOption,
            localOption,
            remoteOption,
            profileOption,
            clearOption,
            forceOption,
            workspaceFormatOption,
            workspaceAuthOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, format, "connect", OutputFormats.Text, OutputFormats.Json))
                return 2;

            // Effective stderr format: explicit --error-format wins, else JSON output implies JSON errors.
            var errorFormat = parseResult.GetValue(GlobalOptions.ErrorFormat)
                ?? (OutputFormats.IsJson(format) ? OutputFormats.Json : null);

            var handler = new ConnectHandler(_state);
            if (GlobalOptions.RecentSpecified(parseResult))
            {
                if (parseResult.GetValue(clearOption))
                    return RenderRecentOptionError("--recent cannot be combined with --clear.");

                if (!string.IsNullOrWhiteSpace(parseResult.GetValue(serverArgument)) ||
                    !string.IsNullOrWhiteSpace(parseResult.GetValue(databaseArgument)) ||
                    !string.IsNullOrWhiteSpace(parseResult.GetValue(profileOption)) ||
                    parseResult.GetResult(workspaceOption) is not null ||
                    parseResult.GetValue(remoteOption) ||
                    parseResult.GetValue(localOption))
                    return RenderRecentOptionError("--recent cannot be combined with a server/database, --profile, --workspace, --remote, or --local.");

                return await ConnectRecentAsync(handler, parseResult, format, cancellationToken);
            }

            if (parseResult.GetValue(clearOption))
                return CommandOutput.Render(
                    handler.Clear(),
                    format,
                    result => AnsiConsole.MarkupLine(result.Cleared
                        ? Styling.Success("Cleared active connection.")
                        : Styling.Muted("No active connection.")));

            // ArgumentArity.ZeroOrOne surfaces both "absent" and "bare -w" as null from GetValue;
            // gate on GetResult so a valueless -w (present, no value) is distinguishable from absent.
            var profileName = parseResult.GetValue(profileOption);
            var request = new ConnectPlanRequest(
                Server: parseResult.GetValue(serverArgument),
                Database: parseResult.GetValue(databaseArgument),
                Profile: profileName,
                WorkspaceValue: parseResult.GetValue(workspaceOption),
                WorkspaceSpecified: parseResult.GetResult(workspaceOption) is not null,
                Local: parseResult.GetValue(localOption),
                Remote: parseResult.GetValue(remoteOption),
                Auth: GlobalOptions.AuthValue(parseResult),
                WorkspaceFormat: parseResult.GetValue(workspaceFormatOption),
                WorkspaceAuth: parseResult.GetValue(workspaceAuthOption),
                CanPrompt: InteractionGate.CanPrompt(parseResult, format));

            if (!string.IsNullOrWhiteSpace(profileName))
            {
                if (!string.IsNullOrWhiteSpace(request.Server) ||
                    !string.IsNullOrWhiteSpace(request.Database) ||
                    request.WorkspaceSpecified || request.Local || request.Remote ||
                    !string.IsNullOrWhiteSpace(request.Auth) ||
                    !string.IsNullOrWhiteSpace(request.WorkspaceFormat) ||
                    !string.IsNullOrWhiteSpace(request.WorkspaceAuth))
                    return RenderWorkspaceOptionError(
                        "--profile cannot be combined with a server/database, --workspace, --local, --remote, or auth/workspace overrides.");

                var resolved = new ProfileHandler(_state).Resolve(profileName);
                if (!resolved.Success)
                    return CommandOutput.Render(resolved, format, errorFormat, _ => { });

                var profile = resolved.Data!.Profile;
                request = request with
                {
                    Server = profile.Model ?? profile.Server,
                    Database = profile.Database,
                    WorkspaceValue = profile.Workspace,
                    WorkspaceSpecified = !string.IsNullOrWhiteSpace(profile.Workspace),
                    Local = profile.Local && string.IsNullOrWhiteSpace(profile.Model),
                    Auth = profile.Auth,
                    WorkspaceFormat = profile.WorkspaceFormat,
                    WorkspaceAuth = profile.WorkspaceAuth
                };
            }

            // Plan/resolve loop: each pass either reports the first missing piece (resolved here
            // by a prompt or Desktop discovery, then folded back into the request) or yields a
            // terminal outcome to act on.
            ConnectPlanResult plan;
            while (true)
            {
                plan = ConnectPlanHandler.Plan(request);
                request = plan.Request;

                if (plan.ReinterpretedWorkspaceValue is { } swallowed)
                    ErrConsole().MarkupLine(Styling.Muted($"Treating '{Styling.MarkupEscape(swallowed)}' as the model; -w had no value."));

                if (plan.Need is not { } need)
                    break;

                switch (need.Kind)
                {
                    case ConnectNeedKind.RemotePick:
                        {
                            var picked = await ResolveRemoteInteractiveAsync(cancellationToken);
                            if (picked is null)
                                return 1;
                            request = request with { Server = picked.Value.Server, Database = picked.Value.Database, DatabaseResolved = true };
                            break;
                        }

                    case ConnectNeedKind.MirrorWorkspace:
                        {
                            var picked = await PickWorkspaceAsync(cancellationToken);
                            if (picked is null)
                                return 1;
                            request = request with { WorkspaceValue = picked.XmlaEndpoint };
                            break;
                        }

                    case ConnectNeedKind.MirrorDatabase:
                        {
                            var suggestion = ConnectPrompts.SuggestMirrorDatabaseName(need.SuggestionModelName!, _cachedUsername());
                            var selection = await PickDatabaseAsync(
                                ModelReference.Remote(need.Endpoint!),
                                allowCreateNew: true, allowWorkspaceOnly: false, suggestion, cancellationToken);
                            if (selection is null)
                                return 1; // listing failed — do not save a half-configured mirror
                            request = request with { Database = selection.Value.Name }; // workspace-only is not offered here
                            break;
                        }

                    case ConnectNeedKind.PrimaryDatabase:
                        {
                            var selection = await PickDatabaseAsync(
                                ModelReference.Remote(need.Endpoint!),
                                allowCreateNew: false, allowWorkspaceOnly: true, null, cancellationToken);
                            if (selection is null)
                                return 1; // listing failed — do not overwrite the active connection
                            request = request with
                            {
                                Database = selection.Value.IsWorkspaceOnly ? null : selection.Value.Name,
                                DatabaseResolved = true
                            };
                            break;
                        }

                    case ConnectNeedKind.MirrorFolder:
                        {
                            var folder = await ErrConsole().PromptAsync(
                                new TextPrompt<string>("Local workspace folder:").DefaultValue(need.SuggestedFolder!),
                                cancellationToken);
                            request = request with { WorkspaceValue = folder.Trim() };
                            break;
                        }

                    case ConnectNeedKind.DesktopDiscovery:
                        {
                            AnsiConsole.MarkupLine(Styling.Value("Discovering Power BI Desktop instances..."));
                            var endpoints = PowerBiDesktopDiscovery.DiscoverEndpoints();
                            if (endpoints.Count == 0)
                            {
                                ErrConsole().MarkupLine(Styling.Error("No running Power BI Desktop instances found. Start Power BI Desktop and open a report, then retry."));
                                return 1;
                            }

                            if (endpoints.Count > 1 && string.IsNullOrWhiteSpace(request.Database))
                            {
                                ErrConsole().MarkupLine(Styling.Error("Multiple Power BI Desktop instances found. Specify a semantic model name."));
                                return 1;
                            }

                            request = request with { Server = endpoints[0] };
                            break;
                        }
                }
            }

            if (plan.UsageError is { } usageError)
                return RenderWorkspaceOptionError(usageError);

            if (plan.InteractionRequired is { } interaction)
                return RenderInteractiveRequired(parseResult, interaction.Message, interaction.Hint);

            if (plan.ShowCurrent)
                return CommandOutput.Render(handler.Show(), format, ConnectRenderer.RenderShow);

            var target = plan.Target!;
            var model = target.Model;
            var remoteServer = target.RemoteServer;
            var database = target.Database;
            var workspace = target.Workspace;
            var force = parseResult.GetValue(forceOption);

            if (target.Validation is { } validation)
            {
                var quiet = parseResult.GetValue(GlobalOptions.Quiet);
                var suppressSpinner = quiet || OutputFormats.IsJson(format) || OutputFormats.IsCsv(format);
                var infoResult = await CliSpinner.RunAsync(
                    "Connecting...",
                    () => new InfoModelHandler(_providers).HandleAsync(
                        new InfoModelRequest(validation),
                        cancellationToken),
                    suppress: suppressSpinner);

                if (!infoResult.Success)
                {
                    ErrorOutput.Write(infoResult.Diagnostics, errorFormat);
                    return infoResult.ExitCode == 0 ? 1 : infoResult.ExitCode;
                }

                if (target.ProbeMirror)
                {
                    var probe = await CliSpinner.RunAsync(
                        "Checking workspace target...",
                        () => new ConnectWorkspaceHandler(_providers).ProbeAsync(
                            new ConnectWorkspaceProbeRequest(workspace!, database!),
                            cancellationToken),
                        suppress: suppressSpinner);

                    if (probe.Status == ConnectWorkspaceProbeStatus.Exists)
                    {
                        database = probe.ResolvedDatabase;

                        if (!force && !ConfirmationHelper.ConfirmOrAbort(
                            "Overwrite workspace target", $"'{database}' on {workspace}",
                            parseResult.GetValue(GlobalOptions.Yes),
                            parseResult.GetValue(GlobalOptions.NonInteractive)))
                        {
                            ErrConsole().MarkupLine(Styling.Guidance(
                                "Aborted; connection unchanged. Re-run without -w to connect without the workspace, or pass --force to overwrite it."));
                            return 1;
                        }
                    }
                    else if (probe.Status == ConnectWorkspaceProbeStatus.Unreachable)
                    {
                        ConnectRenderer.RenderConnectedModel(infoResult.Data!, format, model, remoteServer, database, workspace);
                        foreach (var diag in probe.Diagnostics)
                            ErrConsole().MarkupLine(Styling.Error($"Could not reach workspace server: {Styling.MarkupEscape(ConnectRenderer.WorkspaceConnectMessage(workspace!, diag.Message))}"));
                        return probe.ExitCode == 0 ? 1 : probe.ExitCode;
                    }
                    // Missing: server reachable, target database does not exist yet — OK for new mirror.
                }

                if (target.InitializeWorkspace)
                {
                    var init = await new ConnectWorkspaceHandler(_providers).InitializeAsync(
                        new ConnectWorkspaceInitRequest(workspace!, target.WorkspaceFormat, force, validation),
                        cancellationToken);

                    if (init.Initialized && format == OutputFormats.Text)
                        AnsiConsole.MarkupLine(Styling.Success($"Workspace initialized at {Styling.MarkupEscape(workspace!)} ({init.Serialization})"));
                }

                handler.Set(new ConnectSetRequest(
                    remoteServer,
                    database,
                    model,
                    target.Auth,
                    Local: model is not null || target.Local,
                    target.Profile,
                    workspace,
                    target.WorkspaceFormat,
                    target.WorkspaceAuth));

                if (format != OutputFormats.Text)
                    return CommandOutput.Render(
                        infoResult,
                        format,
                        _ => { },
                        data => ConnectRenderer.ProjectConnectedModelJson(data, model, remoteServer, database, workspace));

                ConnectRenderer.RenderConnectedModelText(infoResult.Data!, model, remoteServer, database, workspace);
                return 0;
            }

            var setResult = handler.Set(new ConnectSetRequest(
                remoteServer,
                database,
                model,
                target.Auth,
                target.Local,
                target.Profile,
                workspace,
                target.WorkspaceFormat,
                target.WorkspaceAuth));

            if (!setResult.Success)
                return CommandOutput.Render(setResult, format, _ => { });

            if (format != OutputFormats.Text)
                return CommandOutput.Render(setResult, format, _ => { });

            ConnectRenderer.RenderConnection(setResult.Data!.Connection);
            return setResult.ExitCode;
        });

        return command;
    }

    private async Task<int> ConnectRecentAsync(
        ConnectHandler handler,
        ParseResult parseResult,
        string format,
        CancellationToken cancellationToken)
    {
        var recents = handler.Recents();
        var connections = recents.Data!.Connections;

        // Bare --recent when a prompt is unavailable (or there is nothing to pick from)
        // lists the recents instead of prompting, so scripts and JSON callers never block.
        var bare = GlobalOptions.RecentValue(parseResult) is null;
        if (bare && (!InteractionGate.CanPrompt(parseResult, format) || connections.Count == 0))
            return CommandOutput.Render(recents, format, RenderRecentList, ProjectRecentListJson);

        if (!RecentConnections.TryResolve(parseResult, _state, out var entry, out var exitCode))
            return exitCode;

        var connection = entry!.Connection;
        ModelReference? validation = null;
        if (!string.IsNullOrWhiteSpace(connection.Model))
            validation = new ModelReference(connection.Model);
        else if (!string.IsNullOrWhiteSpace(connection.Server) && !string.IsNullOrWhiteSpace(connection.Database))
            validation = ModelReference.Remote(connection.Server, connection.Database);

        InfoModelResult? info = null;
        if (validation is not null)
        {
            var infoHandler = new InfoModelHandler(_providers);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var infoResult = await CliSpinner.RunAsync(
                "Connecting...",
                () => infoHandler.HandleAsync(
                    new InfoModelRequest(validation),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(format) || OutputFormats.IsCsv(format));

            if (!infoResult.Success)
            {
                ErrorOutput.Write(
                    infoResult.Diagnostics,
                    parseResult.GetValue(GlobalOptions.ErrorFormat)
                        ?? (OutputFormats.IsJson(format) ? OutputFormats.Json : null));
                return infoResult.ExitCode == 0 ? 1 : infoResult.ExitCode;
            }

            info = infoResult.Data;
        }

        // Replay the snapshot's raw fields rather than its profile name: the recent entry
        // must keep working even if the profile was renamed or deleted since it was recorded.
        var setResult = handler.Set(new ConnectSetRequest(
            connection.Server,
            connection.Database,
            connection.Model,
            connection.Auth,
            connection.Local,
            Profile: null,
            connection.Workspace,
            connection.WorkspaceFormat,
            connection.WorkspaceAuth));

        if (!setResult.Success)
            return CommandOutput.Render(setResult, format, _ => { });

        if (info is not null)
        {
            ConnectRenderer.RenderConnectedModel(info, format, connection.Model, connection.Server, connection.Database, connection.Workspace);
            return 0;
        }

        if (format != OutputFormats.Text)
            return CommandOutput.Render(setResult, format, _ => { });

        ConnectRenderer.RenderConnection(setResult.Data!.Connection);
        return setResult.ExitCode;
    }

    private static void RenderRecentList(ConnectRecentListResult result)
    {
        var err = ErrConsole();
        if (result.Connections.Count == 0)
        {
            AnsiConsole.MarkupLine(Styling.Warning("No recent connections yet."));
            err.MarkupLine(Styling.Guidance("Connect once: tx connect <server> <database>"));
            return;
        }

        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < result.Connections.Count; i++)
        {
            var entry = result.Connections[i];
            AnsiConsole.MarkupLine(
                $"{Styling.Bold($"{i + 1}.")} {Styling.MarkupEscape(RecentConnections.FormatRecentLabel(entry.Connection))}  {Styling.Muted(RecentConnections.FormatRecentAge(entry.LastUsed, now))}");
        }

        err.MarkupLine(Styling.Guidance("Connect: tx connect --recent <n>"));
    }

    private static object ProjectRecentListJson(ConnectRecentListResult result)
        => new
        {
            connections = result.Connections.Select((entry, i) => new
            {
                index = i + 1,
                lastUsed = entry.LastUsed,
                server = entry.Connection.Server,
                database = entry.Connection.Database,
                model = entry.Connection.Model,
                local = entry.Connection.Local,
                profile = entry.Connection.Profile,
                workspace = entry.Connection.Workspace
            }).ToArray()
        };

    private static int RenderRecentOptionError(string message)
    {
        ErrConsole().MarkupLine(Styling.Error(message));
        return 2;
    }

    private static int RenderWorkspaceOptionError(string message)
    {
        ErrConsole().MarkupLine(Styling.Error(Styling.MarkupEscape(message)));
        return 2;
    }

    // An interactive-only flow (--remote, valueless -w) was invoked without a usable TTY. Emit the
    // documented TOMIX_INTERACTIVE_REQUIRED diagnostic through ErrorOutput so --error-format json
    // callers can detect the condition, and exit 1.
    private static int RenderInteractiveRequired(ParseResult parseResult, string message, string hint)
    {
        ErrorOutput.Write(
            new[] { new TomixDiagnostic("TOMIX_INTERACTIVE_REQUIRED", DiagnosticSeverity.Error, message, Hint: hint) },
            parseResult.GetValue(GlobalOptions.ErrorFormat));
        return 1;
    }

    private static IAnsiConsole ErrConsole()
        => AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });

    private async Task<(string Server, string? Database)?> ResolveRemoteInteractiveAsync(CancellationToken cancellationToken)
    {
        var workspace = await PickWorkspaceAsync(cancellationToken);
        if (workspace is null)
            return null;

        var selection = await PickDatabaseAsync(
            ModelReference.Remote(workspace.XmlaEndpoint),
            allowCreateNew: false, allowWorkspaceOnly: true, suggestedNewName: null, cancellationToken);
        if (selection is null)
            return null; // listing failed — do not fall through to "workspace only"

        return (workspace.XmlaEndpoint, selection.Value.IsWorkspaceOnly ? null : selection.Value.Name);
    }

    private async Task<WorkspaceInfo?> PickWorkspaceAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ConnectPrompts.PickWorkspaceAsync(ErrConsole(), _workspaceCatalog, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RenderInteractiveError(ex);
            return null;
        }
    }

    // Returns the model selection, or null on failure (listing error, expired auth, or no catalog).
    // A null here means "could not resolve" and must NOT be treated as an explicit workspace-only
    // choice — callers stop with exit 1 rather than saving a connection.
    private async Task<ConnectPrompts.DatabaseSelection?> PickDatabaseAsync(
        ModelReference endpoint,
        bool allowCreateNew,
        bool allowWorkspaceOnly,
        string? suggestedNewName,
        CancellationToken cancellationToken)
    {
        var catalog = _providers.OfType<IServerCatalog>().FirstOrDefault(c => c.CanList(endpoint));
        if (catalog is null)
        {
            RenderInteractiveError(new InvalidOperationException(
                $"No provider can list models on '{endpoint.Value}'."));
            return null;
        }

        try
        {
            return await ConnectPrompts.PickDatabaseAsync(
                ErrConsole(), catalog, endpoint, allowCreateNew, allowWorkspaceOnly, suggestedNewName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RenderInteractiveError(ex);
            return null;
        }
    }

    private static void RenderInteractiveError(Exception ex)
    {
        var (code, hint) = ex is AuthenticationRequiredException
            ? ("TOMIX_AUTH_REQUIRED", "Run 'tx auth login', then retry.")
            : ("TOMIX_REMOTE_LIST_FAILED", (string?)null);
        ErrorOutput.Write(
            new[] { new TomixDiagnostic(code, DiagnosticSeverity.Error, ex.Message, Hint: hint) },
            null);
    }
}
