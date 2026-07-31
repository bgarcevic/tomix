using System.CommandLine;
using Spectre.Console;
using Tomix.App.Deploy;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.Cli.Commands;

internal sealed class DeployCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    private readonly CliStateStore _state;
    private readonly HttpClient? _httpClient;

    public DeployCommand(
        IReadOnlyList<IModelProvider> providers,
        CliStateStore state,
        HttpClient? httpClient = null)
    {
        _providers = providers;
        _state = state;
        _httpClient = httpClient;
    }

    public Command Build()
    {
        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to model (if not using --model)",
            Arity = ArgumentArity.ZeroOrOne
        };

        var profileOption = new Option<string?>("--profile")
        {
            Description = "Use a saved connection profile for this deploy (one-shot, does not persist as active connection)"
        };
        profileOption.Aliases.Add("-p");

        var createOnlyOption = new Option<bool>("--create-only")
        {
            Description = "Only create new model; fail if it already exists"
        };
        var xmlaOption = new Option<string?>("--xmla")
        {
            Description = "Generate XMLA/TMSL script to file instead of deploying. Use '-' for stdout."
        };
        var skipBpaOption = new Option<bool>("--skip-bpa")
        {
            Description = "Skip BPA gate check (configured via .te-bpa.json)"
        };
        var fixBpaOption = new Option<bool>("--fix-bpa")
        {
            Description = "Auto-fix BPA violations before deploying (applies FixExpressions where available)"
        };
        var bpaRulesOption = new Option<string[]>("--bpa-rules")
        {
            Description = "Path(s) to BPA rule file(s) for this deploy. Overrides bpa.rules in CLI config.",
            Arity = ArgumentArity.ZeroOrMore
        };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Force deployment, bypassing validation checks"
        };
        var ciOption = new Option<string?>("--ci")
        {
            Description = "Emit CI logging commands to stderr: vsts (Azure DevOps), github (GitHub Actions)"
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Preview what would change on the remote target without deploying"
        };
        var deployConnectionsOption = new Option<bool>("--deploy-connections")
        {
            Description = "Overwrite the target's data sources (default: keep the target's connection strings)"
        };
        var deployPartitionsOption = new Option<bool>("--deploy-partitions")
        {
            Description = "Overwrite the target's table partitions (default: keep the target's partitions and processed data)"
        };
        var deployPolicyPartitionsOption = new Option<bool>("--deploy-policy-partitions")
        {
            Description = "With --deploy-partitions: also overwrite incremental-refresh policy partitions, discarding processed data (default: keep them)"
        };
        var deploySharedExpressionsOption = new Option<bool>("--deploy-shared-expressions")
        {
            Description = "Overwrite the target's shared expressions / M parameters (default: keep the target's values)"
        };
        var deployRolesOption = new Option<bool>("--deploy-roles")
        {
            Description = "Overwrite the target's security roles (default: keep the target's roles)"
        };
        var deployRoleMembersOption = new Option<bool>("--deploy-role-members")
        {
            Description = "With --deploy-roles: also overwrite role members (default: keep the target's members)"
        };
        var deployFullOption = new Option<bool>("--deploy-full")
        {
            Description = "Overwrite everything, including incremental-refresh partitions (cannot be combined with other --deploy-* flags)"
        };

        var command = new Command("deploy", "Deploy a semantic model to a workspace (--xmla for script-only, --skip-bpa to bypass)")
        {
            modelArgument,
            profileOption,
            createOnlyOption,
            xmlaOption,
            skipBpaOption,
            fixBpaOption,
            bpaRulesOption,
            forceOption,
            ciOption,
            dryRunOption,
            deployConnectionsOption,
            deployPartitionsOption,
            deployPolicyPartitionsOption,
            deploySharedExpressionsOption,
            deployRolesOption,
            deployRoleMembersOption,
            deployFullOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            var errorFormat = parseResult.GetValue(GlobalOptions.ErrorFormat);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            if (!CommandOutput.TryValidateFormat(parseResult, format, "deploy", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var explicitModel = GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument);
            ModelReference reference;
            if (GlobalOptions.RecentSpecified(parseResult))
            {
                // --recent picks the deploy *source*; --server/--database keep addressing the target.
                if (!string.IsNullOrWhiteSpace(explicitModel))
                {
                    var err = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
                    err.MarkupLine(Styling.Error("--recent cannot be combined with a model path."));
                    return 2;
                }

                if (!RecentConnections.TryResolve(parseResult, _state, out var entry, out var recentExit))
                    return recentExit;

                // Resolve against the picked entry (not the active session) so a server-only
                // recent source does not inherit the active connection's database.
                var recent = entry!.Connection;
                reference = new ActiveModelResolver(() => recent).ResolveReference(recent.Model, recent.Database, recent.Server);
            }
            else
            {
                reference = new ActiveModelResolver(_state).ResolveReference(
                    explicitModel,
                    parseResult.GetValue(GlobalOptions.Database));
            }

            var server = parseResult.GetValue(GlobalOptions.Server);
            var database = parseResult.GetValue(GlobalOptions.Database);
            var dryRun = parseResult.GetValue(dryRunOption);

            var deployFull = parseResult.GetValue(deployFullOption);
            var granularFlags = new Option<bool>[]
            {
                deployConnectionsOption, deployPartitionsOption, deployPolicyPartitionsOption,
                deploySharedExpressionsOption, deployRolesOption, deployRoleMembersOption
            };
            if (deployFull && granularFlags.Any(o => parseResult.GetResult(o) is { Implicit: false }))
            {
                // Same rendering pipeline as the handler's flag-dependency errors so all
                // deploy flag failures look alike (and stay structured under JSON output).
                return CommandOutput.Render(
                    TomixResult<DeployModelResult>.Fail(
                        "TOMIX_DEPLOY_INVALID_FLAGS",
                        "--deploy-full cannot be combined with other --deploy-* flags.",
                        exitCode: 2),
                    format,
                    errorFormat,
                    data => DeployRenderer.Render(data, reference.Value));
            }

            var deployOptions = deployFull
                ? ModelDeployOptions.Full
                : new ModelDeployOptions(
                    DeployConnections: parseResult.GetValue(deployConnectionsOption),
                    DeployPartitions: parseResult.GetValue(deployPartitionsOption),
                    DeploySharedExpressions: parseResult.GetValue(deploySharedExpressionsOption),
                    DeployRoles: parseResult.GetValue(deployRolesOption),
                    DeployRoleMembers: parseResult.GetValue(deployRoleMembersOption),
                    DeployPolicyPartitions: parseResult.GetValue(deployPolicyPartitionsOption));

            if (!dryRun && !ConfirmationHelper.ConfirmOrAbort(
                "Deploy", $"{database ?? reference.Value} to {server ?? "workspace"}",
                parseResult.GetValue(GlobalOptions.Yes),
                parseResult.GetValue(GlobalOptions.NonInteractive)))
                return 1;

            var spinnerLabel = dryRun ? "Previewing deployment..." : "Deploying model...";
            var result = await CliSpinner.RunAsync(
                spinnerLabel,
                () => new DeployModelHandler(_providers, _state, httpClient: _httpClient).HandleAsync(
                    new DeployModelRequest(
                        reference,
                        server,
                        database,
                        parseResult.GetValue(profileOption),
                        parseResult.GetValue(createOnlyOption),
                        parseResult.GetValue(skipBpaOption),
                        parseResult.GetValue(fixBpaOption),
                        parseResult.GetValue(bpaRulesOption),
                        parseResult.GetValue(xmlaOption),
                        parseResult.GetValue(forceOption),
                        parseResult.GetValue(ciOption),
                        dryRun,
                        deployOptions),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(format) || OutputFormats.IsCsv(format));

            if (result.Data is not null && result.Data.ScriptPath == "-" && result.Data.Script is not null)
            {
                if (OutputFormats.IsJson(format))
                    JsonOutput.Write(result.Data);
                else
                    Console.WriteLine(result.Data.Script);
                return result.ExitCode;
            }

            return CommandOutput.Render(
                result,
                format,
                errorFormat,
                data => DeployRenderer.Render(data, reference.Value));
        });

        return command;
    }

}
