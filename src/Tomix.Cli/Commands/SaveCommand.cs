using System.CommandLine;
using Tomix.App.Save;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;
using Spectre.Console;

namespace Tomix.Cli.Commands;

internal sealed class SaveCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public SaveCommand(IReadOnlyList<IModelProvider> providers) => _providers = providers;

    public Command Build()
    {
        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to model, Fabric path, or omit for active connection",
            Arity = ArgumentArity.ZeroOrOne
        };
        var outputPathOption = new Option<string?>("--output-path")
        {
            Description = "File system path to write the model to. Omit to save the loaded model back to its source."
        };
        outputPathOption.Aliases.Add("-o");

        var serializationOption = new Option<string?>("--serialization")
        {
            Description = "Model serialization: tmdl, bim (tmsl and auto also accepted). Defaults to the loaded model's format."
        };
        serializationOption.AcceptAmongIgnoreCase("tmdl", "bim", "tmsl", "auto");
        var forceOption = new Option<bool>("--force")
        {
            Description = "Skip validation and overwrite existing output. Does not override layout-safety refusals."
        };
        var skipBpaOption = new Option<bool>("--skip-bpa")
        {
            Description = "Skip BPA gate check (configured via .te-bpa.json)"
        };
        var fixBpaOption = new Option<bool>("--fix-bpa")
        {
            Description = "Auto-fix BPA violations before saving (applies FixExpressions where available)"
        };
        var bpaRulesOption = new Option<string[]>("--bpa-rules")
        {
            Description = "Path(s) to BPA rule file(s) for this save. Overrides bpa.rules in CLI config.",
            Arity = ArgumentArity.ZeroOrMore
        };
        var skipValidationOption = new Option<bool>("--skip-validation")
        {
            Description = "Skip DAX semantic validation. Faster for pure download-and-save scenarios."
        };
        var supportingFilesOption = new Option<bool>("--supporting-files")
        {
            Description = "Wrap output in a {modelName}.SemanticModel/ folder with .platform and definition.pbism. Only for tmdl/bim on bare targets."
        };
        var noSyncOption = new Option<bool>("--no-sync")
        {
            Description = "Skip workspace sync when workspace mode is active."
        };

        var command = new Command("save", "Save a model to disk in a specified format (like fab export)")
        {
            modelArgument,
            outputPathOption,
            serializationOption,
            forceOption,
            skipBpaOption,
            fixBpaOption,
            bpaRulesOption,
            skipValidationOption,
            supportingFilesOption,
            noSyncOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            var errorFormat = parseResult.GetValue(GlobalOptions.ErrorFormat);
            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "save", OutputFormats.Text, OutputFormats.Json, OutputFormats.Csv))
                return 2;

            var outputPath = parseResult.GetValue(outputPathOption);
            var serialization = parseResult.GetValue(serializationOption) ?? "";
            var force = parseResult.GetValue(forceOption);
            var supportingFiles = parseResult.GetValue(supportingFilesOption);
            var fixBpa = parseResult.GetValue(fixBpaOption);
            var bpaRules = parseResult.GetValue(bpaRulesOption);
            var noSync = parseResult.GetValue(noSyncOption);

            if (!RecentConnections.TryGetSource(
                    parseResult,
                    GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                    out var source,
                    out var recentExit))
                return recentExit;

            // Seed the resolver with the picked --recent entry (if any) so the sync target is the
            // mirror saved with that entry, not the active session's — otherwise `save --recent`
            // could push the recent model to the wrong workspace mirror.
            var resolver = RecentConnections.CreateResolver(source);
            var reference = resolver.ResolveReference(source.Model, source.Database, source.Server);

            var syncTarget = noSync ? null : resolver.ResolveSyncTarget();

            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var result = await CliSpinner.RunAsync(
                "Saving model...",
                () => new SaveModelHandler(_providers).HandleAsync(
                    new SaveModelRequest(
                        reference,
                        outputPath,
                        serialization,
                        force,
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
