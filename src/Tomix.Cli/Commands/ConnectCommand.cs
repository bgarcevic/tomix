using System.CommandLine;
using Tomix.App.Connect;
using Tomix.App.Info;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;
using Spectre.Console;

namespace Tomix.Cli.Commands;

internal sealed class ConnectCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public ConnectCommand(IReadOnlyList<IModelProvider> providers) => _providers = providers;

    public Command Build()
    {
        var serverArgument = new Argument<string>("server")
        {
            Description = "Workspace name, endpoint, local model path (TMDL/BIM), or omit with --local.",
            Arity = ArgumentArity.ZeroOrOne
        };
        var databaseArgument = new Argument<string>("database")
        {
            Description = "Semantic model name (omit to list all models on workspace)",
            Arity = ArgumentArity.ZeroOrOne
        };
        var workspaceOption = new Option<string?>("--workspace")
        {
            Description = "Enable workspace mode: mirror saves between the primary source and a secondary target. Primary remote -> pass a local folder or .bim file. Primary local -> pass <server> <database>."
        };
        workspaceOption.Aliases.Add("-w");
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

        var command = new Command("connect", "Set active connection (workspace, local path, or PBI Desktop). No args = show current.")
        {
            serverArgument,
            databaseArgument,
            workspaceOption,
            profileOption,
            clearOption,
            forceOption,
            workspaceFormatOption,
            workspaceAuthOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(format, "connect", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var handler = new ConnectHandler();
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

            if (local &&
                string.IsNullOrWhiteSpace(database) &&
                !LooksLikeLocalModelPath(server) &&
                !ModelReference.IsRemoteEndpoint(server))
            {
                database = server;
                server = null;
            }

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

    private static int RenderWorkspaceOptionError(string message)
    {
        var err = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
        err.MarkupLine(Styling.Error(Styling.MarkupEscape(message)));
        return 1;
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
