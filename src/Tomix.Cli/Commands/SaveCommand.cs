using System.CommandLine;
using Spectre.Console;
using Tomix.App.Save;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

internal sealed class SaveCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    private readonly CliStateStore _state;
    private readonly HttpClient? _httpClient;

    public SaveCommand(
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
            Description = "Model path, Fabric path, or omit to use the active connection",
            Arity = ArgumentArity.ZeroOrOne
        };
        var outputPathOption = new Option<string?>("--output-file")
        {
            Description = "Where to write the model. Omit to write it back to where it was loaded from."
        };
        outputPathOption.Aliases.Add("-o");

        var serializationOption = new Option<string?>("--serialization")
        {
            Description = "How the model is written: tmdl or bim (tmsl and auto also accepted). Defaults to the loaded model's format."
        };
        serializationOption.AcceptAmongIgnoreCase("tmdl", "bim", "tmsl", "auto");
        var overwriteOption = LifecycleOptions.Overwrite("Replace an existing output file or directory");
        var fixBpaOption = new Option<bool>("--fix-bpa")
        {
            Description = "Apply BPA rule fixes before saving, where a rule provides one. Rules that cannot be evaluated block the save"
        };
        var bpaRulesOption = new Option<string[]>("--bpa-rules")
        {
            Description = "Additional BPA rule files to enforce for this save, alongside the built-in ruleset.",
            Arity = ArgumentArity.ZeroOrMore
        };
        var supportingFilesOption = new Option<bool>("--supporting-files")
        {
            Description = "Write a {modelName}.SemanticModel/ folder (with .platform and definition.pbism) around the output. Only applies to tmdl/bim written to a bare target."
        };
        var noSyncOption = new Option<bool>("--no-sync")
        {
            Description = "Skip workspace sync when workspace mode is active."
        };

        var command = new Command("save", "Write a model to disk in a chosen format")
        {
            modelArgument,
            outputPathOption,
            serializationOption,
            overwriteOption,
            fixBpaOption,
            bpaRulesOption,
            supportingFilesOption,
            noSyncOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            var errorFormat = GlobalOptions.ErrorFormatValue(parseResult, formatValue);
            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "save", OutputFormats.Text, OutputFormats.Json, OutputFormats.Csv))
                return 2;

            var outputPath = parseResult.GetValue(outputPathOption);
            var serialization = parseResult.GetValue(serializationOption) ?? "";
            var overwrite = parseResult.GetValue(overwriteOption);
            var supportingFiles = parseResult.GetValue(supportingFilesOption);
            var fixBpa = parseResult.GetValue(fixBpaOption);
            var bpaRules = parseResult.GetValue(bpaRulesOption);
            var noSync = parseResult.GetValue(noSyncOption);

            if (!RecentConnections.TryGetSource(
                    parseResult,
                    GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                    _state,
                    out var source,
                    out var recentExit))
                return recentExit;

            // Seed the resolver with the picked --recent entry (if any) so the sync target is the
            // mirror saved with that entry, not the active session's — otherwise `save --recent`
            // could push the recent model to the wrong workspace mirror.
            var resolver = RecentConnections.CreateResolver(source, _state);
            var reference = resolver.ResolveReference(source.Model, source.Database, source.Server);

            // The mirror only applies when the model being saved is the session's primary —
            // a one-shot save of an explicit -s/-d source must not deploy over the mirror.
            var syncTarget = noSync ? null : resolver.ResolveSyncTarget(reference);

            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var result = await CliSpinner.RunAsync(
                "Saving model...",
                () => new SaveModelHandler(_providers, _httpClient).HandleAsync(
                    new SaveModelRequest(
                        reference,
                        outputPath,
                        serialization,
                        overwrite,
                        supportingFiles,
                        fixBpa,
                        bpaRules,
                        syncTarget),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue) || OutputFormats.IsCsv(formatValue));

            return CommandOutput.Render(
                result,
                formatValue,
                errorFormat,
                data => Render(data, reference.Value),
                RenderCsv);
        });

        return command;
    }

    private static void Render(SaveModelResult result, string source)
    {
        AnsiConsole.MarkupLine(Styling.KeyValue("Source:", source));
        AnsiConsole.MarkupLine(Styling.Value($"Saving ({result.Format})..."));
        AnsiConsole.MarkupLine(Styling.Success($"Saved: {result.Saved} ({result.Format})"));

        if (result.Synced)
            AnsiConsole.MarkupLine(Styling.Success($"Synced: {Styling.MarkupEscape(result.SyncTarget!)}"));
        else if (result.SyncWarning is not null)
            AnsiConsole.MarkupLine(Styling.Warning(Styling.MarkupEscape(result.SyncWarning)));
    }

    private static void RenderCsv(SaveModelResult result)
    {
        var line = $"Saved: {result.Saved} ({result.Format})";
        if (result.Synced)
            line += $", Synced: {result.SyncTarget}";
        Console.WriteLine(line);
    }
}
