using System.CommandLine;
using Tomix.App.Connect;
using Tomix.App.Info;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Authentication;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;
using Spectre.Console;

namespace Tomix.Cli.Commands;

internal sealed class ConnectCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly IWorkspaceCatalog _workspaceCatalog;
    private readonly Func<string?> _cachedUsername;

    public ConnectCommand(
        IReadOnlyList<IModelProvider> providers,
        IWorkspaceCatalog workspaceCatalog,
        Func<string?> cachedUsername)
    {
        _providers = providers;
        _workspaceCatalog = workspaceCatalog;
        _cachedUsername = cachedUsername;
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

            var handler = new ConnectHandler();
            if (GlobalOptions.RecentSpecified(parseResult))
            {
                if (parseResult.GetValue(clearOption))
                    return RenderRecentOptionError("--recent cannot be combined with --clear.");

                if (!string.IsNullOrWhiteSpace(parseResult.GetValue(serverArgument)) ||
                    !string.IsNullOrWhiteSpace(parseResult.GetValue(databaseArgument)) ||
                    !string.IsNullOrWhiteSpace(parseResult.GetValue(profileOption)) ||
                    parseResult.GetResult(workspaceOption) is not null ||
                    parseResult.GetValue(remoteOption) ||
                    parseResult.GetValue(GlobalOptions.Local))
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

            var server = parseResult.GetValue(serverArgument);
            var database = parseResult.GetValue(databaseArgument);
            var profile = parseResult.GetValue(profileOption);
            var workspace = parseResult.GetValue(workspaceOption);
            var workspaceFormat = parseResult.GetValue(workspaceFormatOption);
            var workspaceAuth = parseResult.GetValue(workspaceAuthOption);
            var force = parseResult.GetValue(forceOption);
            var auth = GlobalOptions.AuthValue(parseResult);
            var local = parseResult.GetValue(GlobalOptions.Local);
            var remote = parseResult.GetValue(remoteOption);
            // ArgumentArity.ZeroOrOne surfaces both "absent" and "bare -w" as null from GetValue;
            // gate on GetResult so a valueless -w (present, no value) is distinguishable from absent.
            var workspacePresent = parseResult.GetResult(workspaceOption) is not null;
            var canPrompt = InteractionGate.CanPrompt(parseResult, format);

            // --remote: interactive server-only connect. Pick a workspace and model from the tenant,
            // then fall through into the normal remote pipeline with those values filled in.
            if (remote)
            {
                if (!string.IsNullOrWhiteSpace(server) || !string.IsNullOrWhiteSpace(database) ||
                    !string.IsNullOrWhiteSpace(profile) || workspacePresent || local)
                    return RenderWorkspaceOptionError("--remote cannot be combined with a server/database, --local, --profile, or --workspace.");

                if (!canPrompt)
                    return RenderInteractiveRequired(
                        parseResult,
                        "connect --remote is interactive and needs a TTY.",
                        "Pass the workspace and model explicitly: tx connect <workspace> <database>");

                var pickedRemote = await ResolveRemoteInteractiveAsync(cancellationToken);
                if (pickedRemote is null)
                    return 1;

                (server, database) = pickedRemote.Value;
            }

            // Recovery: `tx connect -w model.bim` — the ZeroOrOne option greedily consumed the model
            // path as its value. Reinterpret it as the primary model and treat -w as valueless.
            if (ShouldReinterpretWorkspaceAsModel(server, workspace, workspacePresent))
            {
                ErrConsole().MarkupLine(Styling.Muted($"Treating '{Styling.MarkupEscape(workspace!)}' as the model; -w had no value."));
                server = workspace;
                workspace = null;
            }

            var workspaceValueless = workspacePresent && string.IsNullOrWhiteSpace(workspace);

            if (local &&
                string.IsNullOrWhiteSpace(database) &&
                !LooksLikeLocalModelPath(server) &&
                !ModelReference.IsRemoteEndpoint(server))
            {
                database = server;
                server = null;
            }

            // Interactive fill-in for missing pieces (TTY only; non-TTY keeps today's errors below).
            // Fills `workspace` / `database` so the existing validation and mirror logic run unchanged.
            // Skipped for --remote, which already resolved both above (a null database there means the
            // user deliberately chose "workspace only" and must not be re-prompted).
            if (canPrompt && !local && !remote)
            {
                var primaryIsLocalModel = !string.IsNullOrWhiteSpace(server) &&
                                          !ModelReference.IsRemoteEndpoint(server) &&
                                          LooksLikeLocalModelPath(server);

                // Local model + valueless -w: pick the remote mirror workspace.
                if (workspaceValueless && primaryIsLocalModel &&
                    string.IsNullOrWhiteSpace(profile))
                {
                    var picked = await PickWorkspaceAsync(cancellationToken);
                    if (picked is null)
                        return 1;
                    workspace = picked.XmlaEndpoint;
                    workspaceValueless = false;
                }

                // Local model + remote mirror workspace known but no dataset: pick or create one.
                if (primaryIsLocalModel &&
                    !string.IsNullOrWhiteSpace(workspace) &&
                    string.IsNullOrWhiteSpace(database) &&
                    ModelReference.IsRemoteEndpoint(ModelReference.NormalizeEndpoint(workspace)))
                {
                    var suggestion = ConnectPrompts.SuggestMirrorDatabaseName(ModelNameFromPath(server!), _cachedUsername());
                    var selection = await PickDatabaseAsync(
                        ModelReference.Remote(ModelReference.NormalizeEndpoint(workspace)),
                        allowCreateNew: true, allowWorkspaceOnly: false, suggestion, cancellationToken);
                    if (selection is null)
                        return 1; // listing failed — do not save a half-configured mirror
                    database = selection.Value.Name; // workspace-only is not offered here
                }

                // Remote primary without a dataset (no mirror): pick a model, or connect workspace-only.
                if (!primaryIsLocalModel &&
                    !workspacePresent &&
                    !string.IsNullOrWhiteSpace(server) &&
                    ModelReference.IsRemoteEndpoint(ModelReference.NormalizeEndpoint(server)) &&
                    !ModelReference.IsLocalInstanceEndpoint(ModelReference.NormalizeEndpoint(server)) &&
                    string.IsNullOrWhiteSpace(database))
                {
                    var selection = await PickDatabaseAsync(
                        ModelReference.Remote(ModelReference.NormalizeEndpoint(server)),
                        allowCreateNew: false, allowWorkspaceOnly: true, null, cancellationToken);
                    if (selection is null)
                        return 1; // listing failed — do not overwrite the active connection
                    database = selection.Value.IsWorkspaceOnly ? null : selection.Value.Name;
                }

                // Remote primary + valueless -w: -w is a local mirror folder — prompt for the path.
                if (workspaceValueless && !primaryIsLocalModel &&
                    !string.IsNullOrWhiteSpace(server) &&
                    ModelReference.IsRemoteEndpoint(ModelReference.NormalizeEndpoint(server)))
                {
                    var suggested = "./" + (string.IsNullOrWhiteSpace(database) ? "workspace" : SlugPath(database));
                    var folder = await ErrConsole().PromptAsync(
                        new TextPrompt<string>("Local workspace folder:").DefaultValue(suggested),
                        cancellationToken);
                    workspace = folder.Trim();
                    workspaceValueless = false;
                }
            }

            // A valueless -w that could not be resolved interactively (non-TTY, --non-interactive,
            // --quiet, or json/csv output) has no target — surface the same machine-readable
            // TOMIX_INTERACTIVE_REQUIRED diagnostic as --remote, with the flag-equivalent hint.
            if (workspaceValueless)
                return RenderInteractiveRequired(
                    parseResult,
                    "-w with no value needs an interactive terminal to pick the workspace.",
                    "Pass the target explicitly: tx connect <model> <database> -w <workspace>.");

            if (!string.IsNullOrWhiteSpace(workspace))
            {
                if (local)
                    return RenderWorkspaceOptionError("--workspace is not supported with --local (PBI Desktop).");

                if (!string.IsNullOrWhiteSpace(profile))
                    return RenderWorkspaceOptionError("--workspace cannot be combined with --profile. Activate the profile first, then set up workspace mode separately.");

                if (string.IsNullOrWhiteSpace(server) && string.IsNullOrWhiteSpace(database))
                    return RenderWorkspaceOptionError("--workspace requires an explicit primary source (server+database or local path).");

                if (!string.IsNullOrWhiteSpace(server) && string.IsNullOrWhiteSpace(database))
                {
                    var message = LooksLikeLocalModelPath(server)
                        ? "--workspace requires <server> <database> (two values) when the primary is a local path."
                        : "--workspace requires both <server> and <database> for the primary connection.";
                    return RenderWorkspaceOptionError(message);
                }

                if (string.IsNullOrWhiteSpace(workspaceAuth))
                    workspaceAuth = string.IsNullOrWhiteSpace(auth) ? "auto" : auth;
            }
            else
            {
                workspaceFormat = null;
                workspaceAuth = null;
            }

            if (string.IsNullOrWhiteSpace(server) &&
                string.IsNullOrWhiteSpace(database) &&
                string.IsNullOrWhiteSpace(profile) &&
                string.IsNullOrWhiteSpace(workspace) &&
                !local)
            {
                return CommandOutput.Render(handler.Show(), format, RenderShow);
            }

            var isRemoteEndpoint = ModelReference.IsRemoteEndpoint(server);
            var model = (!isRemoteEndpoint && LooksLikeLocalModelPath(server)) ? Path.GetFullPath(server!) : null;
            var remoteServer = model is null ? server : null;

            if (local && model is null && !ModelReference.IsLocalInstanceEndpoint(remoteServer))
            {
                AnsiConsole.MarkupLine(Styling.Value("Discovering Power BI Desktop instances..."));
                var endpoints = DiscoverPowerBiDesktopEndpoints();
                if (endpoints.Count == 0)
                {
                    var err = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
                    err.MarkupLine(Styling.Error("No running Power BI Desktop instances found. Start Power BI Desktop and open a report, then retry."));
                    return 1;
                }

                if (endpoints.Count > 1 && string.IsNullOrWhiteSpace(database))
                {
                    var err = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
                    err.MarkupLine(Styling.Error("Multiple Power BI Desktop instances found. Specify a semantic model name."));
                    return 1;
                }

                remoteServer = endpoints[0];
                isRemoteEndpoint = ModelReference.IsRemoteEndpoint(remoteServer);
            }

            // A bare workspace name (e.g. "MyWorkspace") is shorthand for the workspace's XMLA
            // endpoint. Expand it to a fully-qualified endpoint so the stored connection can be
            // opened by every remote provider (CanOpen checks IsRemote) and validated below when
            // a database is supplied. The TOM provider applies the same normalization at connect
            // time, but expanding here keeps the active connection usable by every later command.
            if (!string.IsNullOrWhiteSpace(remoteServer) && !ModelReference.IsRemoteEndpoint(remoteServer))
            {
                remoteServer = ModelReference.NormalizeEndpoint(remoteServer);
                isRemoteEndpoint = true;
            }

            // Mirror target normalization: a bare workspace name as a mirror target (local primary)
            // is shorthand for the workspace's XMLA endpoint, mirroring the primary-server expansion
            // above. Without this, a bare -w value is stored verbatim, IsRemoteEndpoint returns
            // false, the probe below is skipped, and ResolveSyncTarget later returns null — so the
            // mirror is rendered but never actually reachable. Remote-primary -w values (local
            // folder/.bim paths) are returned unchanged.
            workspace = NormalizeWorkspaceTarget(model, workspace);

            // A bare server that is neither a remote endpoint nor a local model path can never be
            // opened by any provider (the TOM server provider requires a remote endpoint; file/TMDL
            // providers require a path). Reject it here instead of storing a dead-end connection.
            if (!string.IsNullOrWhiteSpace(remoteServer) && !ModelReference.IsRemoteEndpoint(remoteServer))
            {
                ErrorOutput.Write(
                    new[]
                    {
                        new TomixDiagnostic(
                            "TOMIX_CONNECT_INVALID_TARGET",
                            DiagnosticSeverity.Error,
                            $"Not a recognized server endpoint or model path: '{remoteServer}'",
                            Hint: "Pass a workspace name (e.g. MyWorkspace), a workspace URL (powerbi://...), an Analysis Services endpoint (asazure://...), a local TMDL folder or .bim path, or use --local for a running Power BI Desktop instance.")
                    },
                    format);
                return 1;
            }

            // Validate by opening before storing: local model paths always, and remote XMLA
            // endpoints when a dataset is given (so we open that specific catalog, not the whole
            // workspace). A remote endpoint without a dataset is stored as-is without opening.
            ModelReference? validation = null;
            if (string.IsNullOrWhiteSpace(profile))
            {
                if (!string.IsNullOrWhiteSpace(model))
                    validation = new ModelReference(model);
                else if (isRemoteEndpoint && !string.IsNullOrWhiteSpace(database))
                    validation = ModelReference.Remote(remoteServer!, database);
            }

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
                    ErrorOutput.Write(infoResult.Diagnostics, null);
                    return infoResult.ExitCode == 0 ? 1 : infoResult.ExitCode;
                }

                if (model is not null &&
                    !string.IsNullOrWhiteSpace(workspace) &&
                    !string.IsNullOrWhiteSpace(database) &&
                    ModelReference.IsRemoteEndpoint(workspace))
                {
                    var workspaceResult = await CliSpinner.RunAsync(
                        "Checking workspace target...",
                        () => infoHandler.HandleAsync(
                            new InfoModelRequest(ModelReference.Remote(workspace, database)),
                            cancellationToken),
                        suppress: quiet || OutputFormats.IsJson(format) || OutputFormats.IsCsv(format));

                    if (workspaceResult.Success)
                    {
                        // Resolve the canonical dataset name from the remote so the stored mirror
                        // target matches exactly. Power BI rejects XMLA deploys that change a
                        // dataset's name (even casing), so storing the resolved name keeps later
                        // syncs/deploys from being treated as a rename.
                        database = ResolveWorkspaceDatabase(database, workspaceResult.Data!.Summary.DatabaseName);

                        if (!force && !ConfirmationHelper.ConfirmOrAbort(
                            "Overwrite workspace target", $"'{database}' on {workspace}",
                            parseResult.GetValue(GlobalOptions.Yes),
                            parseResult.GetValue(GlobalOptions.NonInteractive)))
                        {
                            handler.Set(new ConnectSetRequest(
                                remoteServer,
                                database,
                                model,
                                auth,
                                Local: model is not null || local,
                                profile,
                                Workspace: null,
                                WorkspaceFormat: null,
                                WorkspaceAuth: null));

                            RenderConnectedModel(infoResult.Data!, format, model, remoteServer, database, workspace: null);
                            AnsiConsole.MarkupLine(Styling.Warning("Workspace setup cancelled."));
                            return 0;
                        }
                    }
                    else if (workspaceResult.Diagnostics.Any(d => d.Code == "TOMIX_DATABASE_NOT_FOUND"))
                    {
                        // Server reachable, target database does not exist yet — OK for new mirror.
                    }
                    else
                    {
                        RenderConnectedModel(infoResult.Data!, format, model, remoteServer, database, workspace);
                        var errConsole = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
                        foreach (var diag in workspaceResult.Diagnostics)
                            errConsole.MarkupLine(Styling.Error($"Could not reach workspace server: {Styling.MarkupEscape(WorkspaceConnectMessage(workspace, diag.Message))}"));
                        return workspaceResult.ExitCode == 0 ? 1 : workspaceResult.ExitCode;
                    }
                }

                if (model is null &&
                    !string.IsNullOrWhiteSpace(workspace) &&
                    !ModelReference.IsRemoteEndpoint(workspace) &&
                    (force || !Directory.Exists(workspace) && !File.Exists(workspace)))
                {
                    var serialization = string.IsNullOrWhiteSpace(workspaceFormat) ? "tmdl" : workspaceFormat.Trim();
                    var wsTarget = serialization.Equals("bim", StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(workspace, "model.bim")
                        : workspace;

                    var wsProvider = _providers.FirstOrDefault(p => p.CanOpen(validation));
                    if (wsProvider is not null)
                    {
                        await using var wsSession = await wsProvider.OpenAsync(validation, cancellationToken);
                        if (wsSession is IModelExportSession wsExporter)
                        {
                            if (force && (Directory.Exists(workspace) || File.Exists(workspace)))
                            {
                                if (Directory.Exists(workspace))
                                    Directory.Delete(workspace, true);
                                else
                                    File.Delete(workspace);
                            }

                            await wsExporter.ExportAsync(
                                new ModelExportRequest(wsTarget, serialization, Force: true, SupportingFiles: false),
                                cancellationToken);

                            if (format == OutputFormats.Text)
                                AnsiConsole.MarkupLine(Styling.Success($"Workspace initialized at {Styling.MarkupEscape(workspace)} ({serialization})"));
                        }
                    }
                }

                handler.Set(new ConnectSetRequest(
                    remoteServer,
                    database,
                    model,
                    auth,
                    Local: model is not null || local,
                    profile,
                    workspace,
                    workspaceFormat,
                    workspaceAuth));

                if (format != OutputFormats.Text)
                    return CommandOutput.Render(
                        infoResult,
                        format,
                        _ => { },
                        data => ProjectConnectedModelJson(data, model, remoteServer, database, workspace));

                RenderConnectedModelText(infoResult.Data!, model, remoteServer, database, workspace);
                return 0;
            }

            var setResult = handler.Set(new ConnectSetRequest(
                remoteServer,
                database,
                model,
                auth,
                local,
                profile,
                workspace,
                workspaceFormat,
                workspaceAuth));

            if (!setResult.Success)
                return CommandOutput.Render(setResult, format, _ => { });

            if (format != OutputFormats.Text)
                return CommandOutput.Render(setResult, format, _ => { });

            RenderConnection(setResult.Data!.Connection);
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

        if (!RecentConnections.TryResolve(parseResult, new CliStateStore(), out var entry, out var exitCode))
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
                ErrorOutput.Write(infoResult.Diagnostics, null);
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
            RenderConnectedModel(info, format, connection.Model, connection.Server, connection.Database, connection.Workspace);
            return 0;
        }

        if (format != OutputFormats.Text)
            return CommandOutput.Render(setResult, format, _ => { });

        RenderConnection(setResult.Data!.Connection);
        return setResult.ExitCode;
    }

    private static void RenderRecentList(ConnectRecentListResult result)
    {
        var err = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
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
        var err = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
        err.MarkupLine(Styling.Error(message));
        return 2;
    }

    private static int RenderWorkspaceOptionError(string message)
    {
        ErrConsole().MarkupLine(Styling.Error(Styling.MarkupEscape(message)));
        return 1;
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

    // Detects `tx connect -w model.bim`, where the ZeroOrOne -w option greedily consumed the model
    // path as its value while the server argument stayed empty. Reinterpreted by the caller as a
    // local primary with a valueless -w.
    internal static bool ShouldReinterpretWorkspaceAsModel(string? server, string? workspaceValue, bool workspacePresent)
        => workspacePresent
           && !string.IsNullOrWhiteSpace(workspaceValue)
           && string.IsNullOrWhiteSpace(server)
           && LooksLikeLocalModelPath(workspaceValue);

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

    // Model name for the autogenerated mirror-dataset suggestion: a .bim/.tmsl file's name without
    // extension, else the containing folder's name (TMDL/TE folder).
    private static string ModelNameFromPath(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var name = Directory.Exists(trimmed)
            ? System.IO.Path.GetFileName(trimmed)
            : System.IO.Path.GetFileNameWithoutExtension(trimmed);
        return string.IsNullOrWhiteSpace(name) ? "model" : name;
    }

    private static string SlugPath(string value)
    {
        var slug = new string(value.Trim().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray());
        return slug.Trim('-') is { Length: > 0 } s ? s : "workspace";
    }

    private static bool LooksLikeLocalModelPath(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           (Directory.Exists(value) || File.Exists(value) || value.Contains('\\') || value.Contains('/'));

    // A bare workspace name as a mirror target (local primary) is shorthand for the workspace's
    // XMLA endpoint, mirroring the primary-server normalization. NormalizeEndpoint expands bare
    // names to powerbi:// and decodes percent escapes (e.g. "sandbox%20bkg"), so both a typed
    // name and a pasted URL resolve to the real workspace. Only applies when the primary is
    // local; a remote primary expects -w to be a local folder/.bim path and is left untouched.
    internal static string? NormalizeWorkspaceTarget(string? model, string? workspace)
    {
        if (model is not null && !string.IsNullOrWhiteSpace(workspace))
        {
            return ModelReference.NormalizeEndpoint(workspace);
        }
        return workspace;
    }

    // Returns the canonical dataset name reported by the remote workspace when available, so the
    // stored mirror target matches exactly. Power BI rejects XMLA deploys that change a dataset's
    // name (even a casing difference), so preferring the resolved name over the user-typed value
    // keeps later syncs/deploys from being treated as a rename. Falls back to the requested name
    // when the remote didn't report one.
    internal static string ResolveWorkspaceDatabase(string? requested, string? resolvedFromRemote)
        => string.IsNullOrWhiteSpace(resolvedFromRemote) ? (requested ?? "") : resolvedFromRemote;

    private static string WorkspaceConnectMessage(string workspace, string message)
    {
        var prefix = $"Could not connect to '{workspace}': ";
        return message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? message[prefix.Length..]
            : message;
    }

    private static void RenderConnectedModel(
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

    private static void RenderConnectedModelText(
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

    private static IReadOnlyList<string> DiscoverPowerBiDesktopEndpoints()
    {
        var roots = PowerBiDesktopWorkspaceRoots()
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var endpoints = new List<string>();

        foreach (var root in roots)
        {
            foreach (var portFile in Directory.EnumerateFiles(root, "msmdsrv.port.txt", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(portFile).Trim();
                if (int.TryParse(text, out var port) && port > 0)
                    endpoints.Add($"localhost:{port}");
            }
        }

        return endpoints.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> PowerBiDesktopWorkspaceRoots()
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Microsoft", "Power BI Desktop", "AnalysisServicesWorkspaces");
            yield return Path.Combine(localAppData, "Packages", "Microsoft.MicrosoftPowerBIDesktop_8wekyb3d8bbwe", "LocalCache", "Microsoft", "Power BI Desktop", "AnalysisServicesWorkspaces");
        }
    }

    private static object ProjectConnectedModelJson(
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

    private static void RenderShow(ConnectShowResult result)
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

    private static void RenderConnection(CliConnectionState connection)
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
}
