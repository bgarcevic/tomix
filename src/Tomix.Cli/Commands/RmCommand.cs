using System.CommandLine;
using Spectre.Console;
using Tomix.App.Mutations;
using Tomix.App.Rm;
using Tomix.App.State;
using Tomix.Cli.Interactive;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

internal sealed class RmCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly SessionScope? _session;

    private readonly CliStateStore _state;
    private readonly MutationStores _mutations;

    public RmCommand(IReadOnlyList<IModelProvider> providers, CliStateStore state, MutationStores mutations, SessionScope? session = null)
    {
        _session = session;
        _providers = providers;
        _state = state;
        _mutations = mutations;
    }

    public Command Build()
    {
        var pathArgument = new Argument<string>("path")
        {
            Description = "Object path to remove"
        };
        var modelArgument = new Argument<string>("model")
        {
            Description = "Optional path to the model; defaults to the active connection",
            Arity = ArgumentArity.ZeroOrOne
        };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Remove even if DAX still references the object, and save even if the change adds validation errors"
        };
        forceOption.Aliases.Add("-f");
        var overwriteOption = LifecycleOptions.Overwrite();
        var ifExistsOption = new Option<bool>("--if-exists")
        {
            Description = "Exit 0 when the object is already gone"
        };
        var saveToOption = LifecycleOptions.SaveTo();
        var serializationOption = LifecycleOptions.Serialization();
        var typeOption = new Option<string?>("--type")
        {
            Description = "Type to pick when the path matches several objects under a table"
        };
        typeOption.Aliases.Add("-t");
        var saveOption = LifecycleOptions.Save();
        var stageOption = LifecycleOptions.Stage();
        var revertOption = LifecycleOptions.Revert();
        var noSyncOption = LifecycleOptions.NoSync();

        var command = new Command("rm", "Delete an object from the model")
        {
            pathArgument,
            modelArgument,
            forceOption,
            overwriteOption,
            ifExistsOption,
            saveToOption,
            serializationOption,
            typeOption,
            saveOption,
            stageOption,
            revertOption,
            noSyncOption
        };
        command.Aliases.Add("remove");

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "rm", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var typeValue = parseResult.GetValue(typeOption);
            ModelObjectKind? type = null;
            if (!string.IsNullOrWhiteSpace(typeValue))
            {
                if (!ModelObjectKindParser.TryParse(typeValue, out var parsed))
                {
                    return TypeValidation.WriteInvalidTypeError(GlobalOptions.ErrorFormatValue(parseResult, formatValue));
                }

                type = parsed;
            }

            var path = parseResult.GetValue(pathArgument) ?? "";
            // Without --save/--save-to/--stage the removal is only previewed, so it asks nothing.
            var persists = parseResult.GetValue(revertOption)
                || LifecycleOptions.Persists(
                    parseResult.GetValue(saveOption), parseResult.GetValue(saveToOption), parseResult.GetValue(stageOption));
            if (persists && !ConfirmationHelper.ConfirmOrAbort(
                "Remove", path, parseResult, formatValue))
                return 1;

            if (!SessionScope.TryResolveModel(
                    _session,
                    parseResult,
                    GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                    _state,
                    out var reference,
                    out var recentExit))
                return recentExit;
            var label = MutationSpinnerLabel.For(
                parseResult.GetValue(saveOption),
                parseResult.GetValue(saveToOption),
                parseResult.GetValue(stageOption),
                parseResult.GetValue(revertOption));
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var result = await CliSpinner.RunAsync(
                label,
                () => (_session is null ? new RemoveModelObjectHandler(_providers, _mutations) : new RemoveModelObjectHandler(_session.Source, _mutations)).HandleAsync(
                    new RemoveModelObjectRequest(
                        reference,
                        path,
                        type,
                        parseResult.GetValue(ifExistsOption),
                        parseResult.GetValue(saveOption),
                        parseResult.GetValue(saveToOption),
                        parseResult.GetValue(serializationOption) ?? "",
                        parseResult.GetValue(forceOption),
                        parseResult.GetValue(stageOption),
                        parseResult.GetValue(revertOption),
                        parseResult.GetValue(noSyncOption),
                        Overwrite: parseResult.GetValue(overwriteOption)),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue));

            var tokens = parseResult.Tokens.Select(t => t.Value).ToList();
            return CommandOutput.Render(parseResult, result, formatValue, data => Render(data, tokens));
        });

        return command;
    }

    private static void Render(RemoveModelObjectResult result, IReadOnlyList<string> tokens)
    {
        if (result.Status == MutationStatus.Reverted)
        {
            AnsiConsole.MarkupLine(Styling.Success("Reverted."));
            return;
        }

        if (result.Status == MutationStatus.Unchanged)
        {
            // The only Changed=false path is --if-exists on a missing object; say so instead of
            // exiting silently.
            if (result.Reason == "not_found" && result.Path is not null)
                AnsiConsole.MarkupLine(Styling.Success(
                    $"Not found: {Styling.MarkupEscape(result.Path)} (nothing removed)"));
            return;
        }

        if (result.RemainingPolicyPartitions is { Count: > 0 } remaining)
            AnsiConsole.MarkupLine(Styling.Warning(
                $"Policy-generated partitions remain on the table: {string.Join(", ", remaining)}."));

        if (result.Status == MutationStatus.Preview)
        {
            AnsiConsole.MarkupLine(Styling.Warning(
                $"Would remove: {Styling.MarkupEscape(result.ObjectPath ?? "")}"));
            if (result.CascadeRemoved is { Count: > 0 } cascadePreview)
                foreach (var item in cascadePreview)
                    AnsiConsole.MarkupLine(Styling.Muted($"Would also remove: {Styling.MarkupEscape(item)}"));

            if (result.BrokenReferences is { Count: > 0 } wouldBreak)
            {
                AnsiConsole.MarkupLine(Styling.Warning(
                    $"Would break {wouldBreak.Count} DAX reference(s) in: "
                    + $"{string.Join(", ", wouldBreak.Select(Styling.MarkupEscape))}."));
            }

            // Blocked: --force alone still only previews and --save alone fails the guard, so
            // give the one command that removes it instead of two half-steps.
            if (result.Reason == "would_block")
                StdErr.MarkupLine(Styling.Guidance("To remove anyway: ") + Styling.Option(ForceSaveHint(tokens)));
            else
                MutationOutput.RenderPersistence(result.Outcome);
            return;
        }

        AnsiConsole.MarkupLine(Styling.Success($"Removed: {result.ObjectPath}"));
        if (result.CascadeRemoved is { Count: > 0 } cascade)
            foreach (var item in cascade)
                AnsiConsole.MarkupLine(Styling.Muted($"Also removed: {item}"));

        if (result.BrokenReferences is { Count: > 0 } broken)
            AnsiConsole.MarkupLine(Styling.Warning(
                $"Warning: {broken.Count} DAX reference(s) to the removed object are now broken: "
                + $"{string.Join(", ", broken)}. Update them with 'tx replace' or inspect with 'tx deps'."));

        MutationOutput.RenderPersistence(result.Outcome);
    }

    /// <summary>The user's own command line plus <c>--force --save</c>, quoted for the host shell.</summary>
    internal static string ForceSaveHint(IReadOnlyList<string> tokens)
        => "tx " + string.Join(" ", tokens
            .Where(t => t is not ("--force" or "-f"))
            .Append("--force")
            .Append("--save")
            .Select(BpaRunView.QuoteToken));
}
