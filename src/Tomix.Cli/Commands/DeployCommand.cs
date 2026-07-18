using System.CommandLine;
using Spectre.Console;
using Tomix.App;
using Tomix.App.Deploy;
using Tomix.App.Diff;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

internal sealed class DeployCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    private readonly AppServices _services;

    public DeployCommand(IReadOnlyList<IModelProvider> providers, AppServices services)
    {
        _providers = providers;
        _services = services;
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

        var deployFullOption = new Option<bool>("--deploy-full")
        {
            Description = "Full deploy: overwrite + connections + partitions + shared expressions + roles + role members"
        };
        var createOnlyOption = new Option<bool>("--create-only")
        {
            Description = "Only create new model; fail if it already exists"
        };
        var deployConnectionsOption = new Option<bool>("--deploy-connections")
        {
            Description = "Deploy data source connections"
        };
        var deployPartitionsOption = new Option<bool>("--deploy-partitions")
        {
            Description = "Deploy partition definitions"
        };
        var skipRefreshPolicyOption = new Option<bool>("--skip-refresh-policy")
        {
            Description = "Skip overwriting partitions with Incremental Refresh Policies (use with --deploy-partitions)"
        };
        var deploySharedExpressionsOption = new Option<bool>("--deploy-shared-expressions")
        {
            Description = "Deploy (overwrite) shared expressions"
        };
        var deployRolesOption = new Option<bool>("--deploy-roles")
        {
            Description = "Deploy security roles"
        };
        var deployRoleMembersOption = new Option<bool>("--deploy-role-members")
        {
            Description = "Deploy role members"
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

        var command = new Command("deploy", "Deploy a semantic model to a workspace (--xmla for script-only, --skip-bpa to bypass)")
        {
            modelArgument,
            profileOption,
            deployFullOption,
            createOnlyOption,
            deployConnectionsOption,
            deployPartitionsOption,
            skipRefreshPolicyOption,
            deploySharedExpressionsOption,
            deployRolesOption,
            deployRoleMembersOption,
            xmlaOption,
            skipBpaOption,
            fixBpaOption,
            bpaRulesOption,
            forceOption,
            ciOption,
            dryRunOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
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

                if (!RecentConnections.TryResolve(parseResult, _services.State, out var entry, out var recentExit))
                    return recentExit;

                // Resolve against the picked entry (not the active session) so a server-only
                // recent source does not inherit the active connection's database.
                var recent = entry!.Connection;
                reference = new ActiveModelResolver(() => recent).ResolveReference(recent.Model, recent.Database, recent.Server);
            }
            else
            {
                reference = new ActiveModelResolver(_services.State).ResolveReference(
                    explicitModel,
                    parseResult.GetValue(GlobalOptions.Database));
            }

            var server = parseResult.GetValue(GlobalOptions.Server);
            var database = parseResult.GetValue(GlobalOptions.Database);
            var dryRun = parseResult.GetValue(dryRunOption);

            if (!dryRun && !ConfirmationHelper.ConfirmOrAbort(
                "Deploy", $"{database ?? reference.Value} to {server ?? "workspace"}",
                parseResult.GetValue(GlobalOptions.Yes),
                parseResult.GetValue(GlobalOptions.NonInteractive)))
                return 1;

            var spinnerLabel = dryRun ? "Previewing deployment..." : "Deploying model...";
            var result = await CliSpinner.RunAsync(
                spinnerLabel,
                () => new DeployModelHandler(_providers, _services.State).HandleAsync(
                    new DeployModelRequest(
                        reference,
                        server,
                        database,
                        parseResult.GetValue(profileOption),
                        parseResult.GetValue(deployFullOption),
                        parseResult.GetValue(createOnlyOption),
                        parseResult.GetValue(skipBpaOption),
                        parseResult.GetValue(fixBpaOption),
                        parseResult.GetValue(bpaRulesOption),
                        parseResult.GetValue(xmlaOption),
                        parseResult.GetValue(forceOption),
                        parseResult.GetValue(ciOption),
                        dryRun),
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
                data => Render(data, reference.Value));
        });

        return command;
    }

    private static void Render(DeployModelResult result, string source)
    {
        if (result.Status == "script")
        {
            AnsiConsole.MarkupLine(Styling.KeyValue("Source:", source));
            AnsiConsole.MarkupLine(Styling.KeyValue("Script:", result.ScriptPath ?? ""));
            return;
        }

        if (result.Status == "dry-run")
        {
            var name = string.IsNullOrWhiteSpace(result.Database) ? source : result.Database;
            AnsiConsole.MarkupLine(Styling.Value($"Dry run: {name} to {result.Server} / {result.Database}"));

            if (result.Diff is not null)
            {
                if (!result.Diff.HasChanges)
                {
                    AnsiConsole.MarkupLine(Styling.Success("No changes — local and remote are identical."));
                    return;
                }

                var s = result.Diff.Summary;
                AnsiConsole.MarkupLine(Styling.Bold(
                    $"{s.Added} added, {s.Removed} removed, {s.Modified} modified"));
                AnsiConsole.WriteLine();

                foreach (var change in result.Diff.Changes)
                    RenderDiffChange(change);
            }
            else
            {
                AnsiConsole.MarkupLine(Styling.Muted("Remote target not reachable or not specified — showing deploy plan only."));
            }

            return;
        }

        var deployName = string.IsNullOrWhiteSpace(result.Database) ? source : result.Database;
        AnsiConsole.MarkupLine(Styling.Value($"Deploying {deployName} to {result.Server} / {result.Database}..."));
        AnsiConsole.MarkupLine(Styling.Success($"Deployed: {result.Status} ({result.DurationMs}ms)"));
    }

    private static void RenderDiffChange(DiffChange change)
    {
        switch (change.Action)
        {
            case "added":
                AnsiConsole.MarkupLine($"  {Styling.Success("+")} {Styling.MarkupEscape(change.ObjectType)} {Styling.Path(change.Path)}");
                break;
            case "removed":
                AnsiConsole.MarkupLine($"  {Styling.Error("-")} {Styling.MarkupEscape(change.ObjectType)} {Styling.Path(change.Path)}");
                break;
            case "modified":
                AnsiConsole.MarkupLine($"  {Styling.Warning("~")} {Styling.MarkupEscape(change.ObjectType)} {Styling.Path(change.Path)}");
                AnsiConsole.MarkupLine($"    {Styling.Error($"- {change.OldValue}")}");
                AnsiConsole.MarkupLine($"    {Styling.Success($"+ {change.NewValue}")}");
                break;
        }
    }
}
