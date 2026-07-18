using System.CommandLine;
using Spectre.Console;
using Tomix.App.Stage;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

/// <summary>
/// <c>stage status | list | discard</c> — inspect and clear the working copies that <c>--stage</c>
/// accumulates for the active model. <c>stage commit</c> (promotion) is added separately.
/// </summary>
internal sealed class StageCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public StageCommand(IReadOnlyList<IModelProvider> providers) => _providers = providers;

    public Command Build()
    {
        var command = new Command("stage", "Inspect and manage staged (uncommitted) model mutations");
        command.SetAction(parseResult => RenderStatus(parseResult));
        command.Subcommands.Add(BuildStatus());
        command.Subcommands.Add(BuildList());
        command.Subcommands.Add(BuildDiscard());
        command.Subcommands.Add(BuildCommit());
        return command;
    }

    private Command BuildCommit()
    {
        var forceOption = new Option<bool>("--force")
        {
            Description = "Commit even if the source changed since staging began (overwrites it)."
        };
        var command = new Command("commit", "Promote staged mutations onto the source (and workspace mirror)")
        {
            forceOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            if (!CommandOutput.TryValidateFormat(parseResult, format, "stage commit", OutputFormats.Text, OutputFormats.Json))
                return 2;

            if (!TryResolveModel(parseResult, out var reference, out var recentExit))
                return recentExit;

            var result = await CliSpinner.RunAsync(
                "Committing staged changes...",
                () => new StageHandler().CommitAsync(
                    reference, _providers, parseResult.GetValue(forceOption), cancellationToken),
                suppress: quiet || OutputFormats.IsJson(format) || OutputFormats.IsCsv(format));
            return CommandOutput.Render(result, format, RenderCommit);
        });
        return command;
    }

    private static Command BuildStatus()
    {
        var command = new Command("status", "Show staged mutations for the active model");
        command.SetAction(parseResult => RenderStatus(parseResult));
        return command;
    }

    private static Command BuildList()
    {
        var command = new Command("list", "List all staged models in the current session");
        command.SetAction(parseResult =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, format, "stage list", OutputFormats.Text, OutputFormats.Json))
                return 2;

            return CommandOutput.Render(new StageHandler().List(), format, RenderList);
        });
        return command;
    }

    private static Command BuildDiscard()
    {
        var allOption = new Option<bool>("--all")
        {
            Description = "Discard staged mutations for every model in the session, not just the active one"
        };
        var command = new Command("discard", "Discard staged mutations without committing them")
        {
            allOption
        };

        command.SetAction(parseResult =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, format, "stage discard", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var all = parseResult.GetValue(allOption);
            if (!TryResolveModel(parseResult, out var reference, out var recentExit))
                return recentExit;
            return CommandOutput.Render(
                new StageHandler().Discard(reference, all),
                format,
                result => AnsiConsole.MarkupLine(Styling.Success($"Discarded {result.Discarded} staged change set(s).")));
        });
        return command;
    }

    private static int RenderStatus(ParseResult parseResult)
    {
        var format = GlobalOptions.OutputFormatValue(parseResult);
        if (!CommandOutput.TryValidateFormat(parseResult, format, "stage", OutputFormats.Text, OutputFormats.Json))
            return 2;

        if (!TryResolveModel(parseResult, out var reference, out var recentExit))
            return recentExit;
        return CommandOutput.Render(new StageHandler().Status(reference), format, RenderStatusResult);
    }

    private static void RenderStatusResult(StageStatusResult result)
    {
        if (!result.Staged)
        {
            AnsiConsole.MarkupLine(Styling.Muted($"Nothing staged for {result.Source}."));
            return;
        }

        AnsiConsole.MarkupLine(Styling.KeyValue("Source:", $"   {result.Source}"));
        AnsiConsole.MarkupLine(Styling.KeyValue("Staged:", $"   {result.WorkingCopy}  ({result.Serialization})"));
        if (result.Workspace)
            AnsiConsole.MarkupLine(Styling.Value($"Workspace: yes (commit syncs local + remote)"));
        AnsiConsole.MarkupLine(Styling.KeyValue("Updated:", $"  {result.UpdatedUtc?.ToLocalTime():yyyy-MM-dd HH:mm:ss}"));
        AnsiConsole.MarkupLine(Styling.KeyValue("Ops:", $"      {result.OpCount}"));
        foreach (var op in result.Ops)
            AnsiConsole.WriteLine($"  {op.Seq}. {op.Summary}");
        AnsiConsole.MarkupLine(Styling.Guidance("Run 'tx stage commit' to promote, or 'tx stage discard' to drop."));
    }

    private static void RenderList(StageListResult result)
    {
        if (result.Staged.Count == 0)
        {
            AnsiConsole.MarkupLine(Styling.Muted("No staged models in this session."));
            return;
        }

        foreach (var entry in result.Staged)
            AnsiConsole.WriteLine($"{entry.Source}\t{entry.OpCount} op(s)\t{entry.UpdatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
    }

    private static void RenderCommit(StageCommitResult result)
    {
        AnsiConsole.MarkupLine(Styling.Success($"Committed {result.OpsCommitted} staged change(s)."));
        if (result.LocalSaved is not null)
            AnsiConsole.MarkupLine(Styling.KeyValue("Local:", $"  {result.LocalSaved}"));
        if (result.RemoteDeployed)
            AnsiConsole.MarkupLine(Styling.KeyValue("Remote:", $" {result.Server}{(string.IsNullOrEmpty(result.Database) ? "" : $" / {result.Database}")}"));
    }

    private static bool TryResolveModel(ParseResult parseResult, out ModelReference reference, out int exitCode)
    {
        if (!RecentConnections.TryGetSource(parseResult, GlobalOptions.ModelValue(parseResult), out var source, out exitCode))
        {
            reference = new ModelReference("");
            return false;
        }

        // Stage resolution deliberately ignores --server today; a server only takes part
        // when it comes from the --recent override.
        reference = GlobalOptions.RecentSpecified(parseResult)
            ? RecentConnections.CreateResolver(source).ResolveReference(source.Model, source.Database, source.Server)
            : new ActiveModelResolver().ResolveReference(source.Model, source.Database);
        return true;
    }
}
