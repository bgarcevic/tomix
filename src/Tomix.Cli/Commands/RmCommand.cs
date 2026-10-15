using System.CommandLine;
using Spectre.Console;
using Tomix.App.Mutations;
using Tomix.App.Rm;
using Tomix.App.State;
using Tomix.Cli.Interactive;
using Tomix.Cli.Output;
using Tomix.Core.Diagnostics;
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
        var pathArgument = new Argument<string[]>("path")
        {
            Description = "Object paths to remove, together as one change, optionally followed by the model "
                + "(an existing folder or file; defaults to the active connection)",
            Arity = ArgumentArity.ZeroOrMore
        };
        var fromOption = new Option<string?>("--from")
        {
            Description = "Read objects to remove from a file, or '-' for stdin: one path per line, or JSON Lines "
                + "of {\"type\", \"table\", \"name\"} or {\"type\", \"path\"} (unknown fields are ignored)",
            HelpName = "file|-"
        };
        var cascadeOption = new Option<bool>("--cascade")
        {
            Description = "Let a batch also change objects it leaves behind: relationships, sort-by, hierarchy levels, "
                + "perspectives, and role permissions pointing at removed objects are removed or cleared"
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
            fromOption,
            cascadeOption,
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

            var (paths, modelPath) = SplitModelArgument(
                parseResult.GetValue(pathArgument) ?? [], GlobalOptions.ModelValue(parseResult) is not null);
            var from = parseResult.GetValue(fromOption);
            var errorFormat = GlobalOptions.ErrorFormatValue(parseResult, formatValue);
            var batch = from is not null || paths.Count > 1;
            var targets = paths.Select(p => new RemoveTarget(p, type, p)).ToList();
            if (from is not null)
            {
                string text;
                try
                {
                    text = InputValueResolver.Resolve(from == "-" ? "-" : null, from == "-" ? null : from) ?? "";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return InputError($"Cannot read --from '{from}': {ex.Message}", errorFormat);
                }

                var parsed = RemoveTargetList.Parse(text);
                if (parsed.Errors.Count > 0)
                    return InputError($"Invalid --from input: {string.Join("; ", parsed.Errors)}", errorFormat);
                targets.AddRange(parsed.Targets.Select(t => t.Type is null && type is not null ? t with { Type = type } : t));
            }

            var revert = parseResult.GetValue(revertOption);
            if (targets.Count == 0 && !revert)
                return InputError("No objects to remove.", errorFormat);

            // Without --save/--save-to/--stage the removal is only previewed, so it asks nothing.
            var persists = revert
                || LifecycleOptions.Persists(
                    parseResult.GetValue(saveOption), parseResult.GetValue(saveToOption), parseResult.GetValue(stageOption));
            var subject = targets.Count == 1 ? targets[0].Label : $"{targets.Count} objects";
            if (persists && !ConfirmationHelper.ConfirmOrAbort(
                "Remove", subject, parseResult, formatValue))
                return 1;

            if (!SessionScope.TryResolveModel(
                    _session,
                    parseResult,
                    GlobalOptions.ModelValue(parseResult) ?? modelPath,
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
            var tokens = parseResult.Tokens.Select(t => t.Value).ToList();
            if (batch)
            {
                var batchResult = await CliSpinner.RunAsync(
                    label,
                    () => (_session is null ? new RemoveBatchHandler(_providers, _mutations) : new RemoveBatchHandler(_session.Source, _mutations)).HandleAsync(
                        new RemoveBatchRequest(
                            reference,
                            targets,
                            parseResult.GetValue(ifExistsOption),
                            parseResult.GetValue(saveOption),
                            parseResult.GetValue(saveToOption),
                            parseResult.GetValue(serializationOption) ?? "",
                            parseResult.GetValue(forceOption),
                            parseResult.GetValue(cascadeOption),
                            parseResult.GetValue(stageOption),
                            revert,
                            parseResult.GetValue(noSyncOption),
                            Overwrite: parseResult.GetValue(overwriteOption)),
                        cancellationToken),
                    suppress: quiet || OutputFormats.IsJson(formatValue));
                return CommandOutput.Render(parseResult, batchResult, formatValue, data => RenderBatch(data, tokens));
            }

            var path = targets.Count == 1 ? targets[0].Path : "";
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
                        revert,
                        parseResult.GetValue(noSyncOption),
                        Overwrite: parseResult.GetValue(overwriteOption)),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue));

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

            if (result.BrokenMReferences is { Count: > 0 } wouldBreakM)
                AnsiConsole.MarkupLine(Styling.Warning(
                    $"Would break {wouldBreakM.Count} Power Query (M) reference(s) in: {string.Join(", ", wouldBreakM)}."));

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

        if (result.BrokenMReferences is { Count: > 0 } brokenM)
            AnsiConsole.MarkupLine(Styling.Warning(
                $"Warning: {brokenM.Count} Power Query (M) reference(s) to the removed object are now broken: {string.Join(", ", brokenM)}."));

        MutationOutput.RenderPersistence(result.Outcome);
    }

    private static void RenderBatch(RemoveBatchResult result, IReadOnlyList<string> tokens)
    {
        if (result.Status == MutationStatus.Reverted)
        {
            AnsiConsole.MarkupLine(Styling.Success("Reverted."));
            return;
        }

        var preview = result.Status == MutationStatus.Preview;
        var removing = result.Targets.Where(t => t.Action == RemoveBatchTarget.Remove).ToList();
        if (result.Status == MutationStatus.Unchanged || removing.Count == 0 && result.Reason is null)
        {
            AnsiConsole.MarkupLine(Styling.Success("Nothing removed: none of the objects exist."));
            return;
        }

        var heading = $"{(preview ? "Would remove" : "Removed")} {removing.Count} object(s):";
        AnsiConsole.MarkupLine(preview ? Styling.Warning(heading) : Styling.Success(heading));
        foreach (var target in result.Targets)
        {
            var line = target.Action switch
            {
                RemoveBatchTarget.Remove => $"  {Styling.MarkupEscape(target.Path ?? "")} {Styling.Muted($"({target.Type})")}",
                RemoveBatchTarget.Subsumed => Styling.Muted($"  {target.Path} ({target.Type}, with {target.SubsumedBy})"),
                _ => Styling.Muted($"  Not found: {target.Input} (skipped)")
            };
            AnsiConsole.MarkupLine(line);
        }

        if (result.Cascades is { Count: > 0 } cascades)
        {
            AnsiConsole.MarkupLine(Styling.Muted(preview ? "Would also change:" : "Also changed:"));
            foreach (var cascade in cascades)
                AnsiConsole.MarkupLine(Styling.Muted($"  {cascade.Description} (from {cascade.Target})"));
        }

        if (result.BrokenReferences is { Count: > 0 } broken)
            AnsiConsole.MarkupLine(Styling.Warning(
                (preview ? $"Would break {broken.Count} DAX reference(s) in: " : $"Warning: {broken.Count} DAX reference(s) are now broken in: ")
                + string.Join(", ", broken) + "."));

        if (result.BrokenMReferences is { Count: > 0 } brokenM)
            AnsiConsole.MarkupLine(Styling.Warning(
                (preview ? $"Would break {brokenM.Count} Power Query (M) reference(s) in: " : $"Warning: {brokenM.Count} Power Query (M) reference(s) are now broken in: ")
                + string.Join(", ", brokenM) + "."));

        if (result.BoundPartitions is { Count: > 0 } bound)
            AnsiConsole.MarkupLine(Styling.Warning(
                $"A data source is still bound to partition(s): {string.Join(", ", bound)}."));

        if (result.BlockedBy is { Count: > 0 } blockedBy)
        {
            if (blockedBy.Contains(RemoveBatchResult.BlockedByBoundPartitions))
                StdErr.MarkupLine(Styling.Guidance("Add the bound partitions to the batch, or repoint them, before removing the data source."));

            var flags = new List<string>();
            if (blockedBy.Contains(RemoveBatchResult.BlockedByReferences) || blockedBy.Contains(RemoveBatchResult.BlockedByMReferences))
                flags.Add("--force");
            if (blockedBy.Contains(RemoveBatchResult.BlockedByCascade))
                flags.Add("--cascade");
            if (flags.Count > 0 && !blockedBy.Contains(RemoveBatchResult.BlockedByBoundPartitions))
                StdErr.MarkupLine(Styling.Guidance("To remove anyway: ") + Styling.Option(SaveHint(tokens, flags)));
            return;
        }

        MutationOutput.RenderPersistence(result.Outcome);
    }

    /// <summary>
    /// Splits the positional values into object paths and the optional trailing model. The last
    /// value is the model only when there are several, <c>--model</c> was not given, and it names
    /// a folder or file that exists — object paths do not — so <c>tx rm Sales/Old ./model</c>
    /// keeps working beside <c>tx rm Sales/Old Sales/Older</c>.
    /// </summary>
    internal static (IReadOnlyList<string> Paths, string? Model) SplitModelArgument(IReadOnlyList<string> values, bool modelOptionGiven)
    {
        if (values.Count >= 2 && !modelOptionGiven && (Directory.Exists(values[^1]) || File.Exists(values[^1])))
            return (values.Take(values.Count - 1).ToList(), values[^1]);

        return (values, null);
    }

    private static int InputError(string message, string? errorFormat)
    {
        ErrorOutput.Write(
            [new TomixDiagnostic(
                "TOMIX_RM_INPUT_INVALID",
                DiagnosticSeverity.Error,
                message,
                "Pass object paths as arguments, or --from <file|-> with one path or JSON object per line.")],
            errorFormat);
        return 2;
    }

    /// <summary>The user's own command line plus <c>--force --save</c>, quoted for the host shell.</summary>
    internal static string ForceSaveHint(IReadOnlyList<string> tokens)
        => SaveHint(tokens, ["--force"]);

    /// <summary>The user's own command line plus <paramref name="flags"/> and <c>--save</c>.</summary>
    internal static string SaveHint(IReadOnlyList<string> tokens, IReadOnlyList<string> flags)
        => "tx " + string.Join(" ", tokens
            .Where(t => t is not ("--force" or "-f") && !flags.Contains(t))
            .Concat(flags)
            .Append("--save")
            .Select(BpaRunView.QuoteToken));
}
