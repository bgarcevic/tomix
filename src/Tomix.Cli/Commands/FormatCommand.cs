using System.CommandLine;
using Spectre.Console;
using Tomix.App.Config;
using Tomix.App.Format;
using Tomix.App.Mutations;
using Tomix.App.State;
using Tomix.Cli.Interactive;
using Tomix.Cli.Output;
using Tomix.Core.Dax;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

internal sealed class FormatCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly SessionScope? _session;
    private readonly IExpressionFormatterClient _formatter;

    private readonly CliStateStore _state;
    private readonly MutationStores _mutations;
    private readonly TomixConfigStore? _config;

    public FormatCommand(
        IReadOnlyList<IModelProvider> providers,
        IExpressionFormatterClient formatter,
        CliStateStore state,
        MutationStores mutations,
        SessionScope? session = null,
        TomixConfigStore? config = null)
    {
        _session = session;
        _config = config;
        _providers = providers;
        _formatter = formatter;
        _state = state;
        _mutations = mutations;
    }

    public Command Build()
    {
        var modelArgument = new Argument<string>("model")
        {
            Description = "Optional path to the model; defaults to the active connection",
            Arity = ArgumentArity.ZeroOrOne
        };
        var expressionOption = new Option<string?>("--expression")
        {
            Description = "The expression to format, given inline"
        };
        expressionOption.Aliases.Add("-e");
        var pathOption = new Option<string?>("--path")
        {
            Description = "Format the expression on a model object path"
        };
        var longOption = new Option<bool>("--long")
        {
            Description = "Long lines: wrap DAX at 120 columns instead of 65, and M at 120 instead of 40"
        };
        var shortOption = new Option<bool>("--short")
        {
            Description = "Short lines (the default); overrides format.lines=long in config"
        };
        var separatorsOption = new Option<string?>("--separators")
        {
            Description = "Display-only DAX separators: us (A, B, 1.5) or eu (A; B; 1,5); models always store us",
            HelpName = "us|eu"
        };
        separatorsOption.AcceptAmongIgnoreCase([.. FormatStyle.SeparatorValues]);
        var functionSpacingOption = new Option<string?>("--function-spacing")
        {
            Description = "Space between a DAX function name and its parenthesis: space (IF ( ) or none (IF( )",
            HelpName = "space|none"
        };
        functionSpacingOption.AcceptAmongIgnoreCase([.. FormatStyle.FunctionSpacingValues]);
        var saveToOption = LifecycleOptions.SaveTo();
        var langOption = new Option<string?>("--lang")
        {
            Description = "Expression language: dax or m"
        };
        var typeOption = new Option<string?>("--type")
        {
            Description = "Type to pick when the path matches several objects under a table"
        };
        typeOption.Aliases.Add("-t");
        var saveOption = LifecycleOptions.Save();
        var forceOption = LifecycleOptions.Force();
        var overwriteOption = LifecycleOptions.Overwrite();
        var stageOption = LifecycleOptions.Stage();
        var revertOption = LifecycleOptions.Revert();
        var noSyncOption = LifecycleOptions.NoSync();
        var checkOption = new Option<bool>("--check")
        {
            Description = "Change nothing; exit 1 when an expression is not formatted (for CI)"
        };

        var command = new Command("format", "Pretty-print DAX and M expressions")
        {
            modelArgument,
            expressionOption,
            pathOption,
            longOption,
            shortOption,
            separatorsOption,
            functionSpacingOption,
            saveToOption,
            langOption,
            typeOption,
            saveOption,
            forceOption,
            overwriteOption,
            stageOption,
            revertOption,
            noSyncOption,
            checkOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "format", OutputFormats.Text, OutputFormats.Json))
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

            var writes = parseResult.GetValue(saveOption)
                         || !string.IsNullOrWhiteSpace(parseResult.GetValue(saveToOption))
                         || parseResult.GetValue(stageOption)
                         || parseResult.GetValue(revertOption);
            var check = parseResult.GetValue(checkOption);
            if (check && writes)
            {
                WriteUsageError(
                    "TOMIX_USAGE",
                    "--check cannot be combined with --save, --save-to, --stage or --revert.",
                    "Use --check in CI to fail on unformatted expressions, and --save to format them.",
                    GlobalOptions.ErrorFormatValue(parseResult, formatValue));
                return 2;
            }

            if (!TryResolveStyle(
                    parseResult.GetValue(separatorsOption),
                    parseResult.GetValue(functionSpacingOption),
                    parseResult.GetValue(longOption),
                    parseResult.GetValue(shortOption),
                    writes,
                    GlobalOptions.ErrorFormatValue(parseResult, formatValue),
                    out var style))
                return 2;

            var modelValue = GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument);
            var expression = InputValueResolver.Resolve(
                parseResult.GetValue(expressionOption),
                readPipedInput: ReadsPipedExpression(
                    modelValue,
                    parseResult.GetValue(pathOption),
                    writes));
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            if (!SessionScope.TryResolveModel(
                    _session,
                    parseResult,
                    modelValue,
                    _state,
                    out var model,
                    out var recentExit))
                return recentExit;

            var result = await CliSpinner.RunAsync(
                "Formatting...",
                () => (_session is null ? new FormatModelHandler(_providers, _formatter, _mutations) : new FormatModelHandler(_session.Source, _formatter, _mutations)).HandleAsync(
                    new FormatModelRequest(
                        model,
                        expression,
                        parseResult.GetValue(pathOption),
                        parseResult.GetValue(langOption) ?? "",
                        type,
                        style.Long,
                        parseResult.GetValue(saveOption),
                        parseResult.GetValue(saveToOption),
                        Serialization: "",
                        Force: parseResult.GetValue(forceOption),
                        Overwrite: parseResult.GetValue(overwriteOption),
                        Stage: parseResult.GetValue(stageOption),
                        Revert: parseResult.GetValue(revertOption),
                        NoSync: parseResult.GetValue(noSyncOption),
                        Separators: style.Separators,
                        FunctionSpacing: style.FunctionSpacing,
                        Check: check),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue) || OutputFormats.IsCsv(formatValue));

            var errorFormat = GlobalOptions.ErrorFormatValue(parseResult, formatValue);
            var exitCode = CommandOutput.Render(
                result,
                formatValue,
                data => Render(data, check),
                data => (object)data,
                renderCsv: null,
                errorFormat);

            if (!result.Success && !quiet && !string.IsNullOrWhiteSpace(expression) && !OutputFormats.IsJson(errorFormat ?? ""))
                WriteInlineCaret(expression, result.Diagnostics);

            return exitCode;
        });

        return command;
    }

    /// <summary>
    /// The style to format with: each flag overrides its <c>format.*</c> config key. EU separators
    /// are display only, so asking for them explicitly alongside a write is rejected rather than
    /// letting the user believe the model now stores them; from config they simply stay on screen.
    /// </summary>
    private bool TryResolveStyle(
        string? separators,
        string? functionSpacing,
        bool longLines,
        bool shortLines,
        bool writes,
        string? errorFormat,
        out FormatStyle style)
    {
        style = FormatStyle.Default;
        if (longLines && shortLines)
        {
            WriteUsageError("TOMIX_USAGE", "--long and --short cannot be combined.", "Pick one line length.", errorFormat);
            return false;
        }

        style = _config is null ? FormatStyle.Default : FormatStyle.FromConfig(_config.Load());
        if (FormatStyle.TryParseSeparators(separators, out var parsedSeparators))
        {
            if (writes && parsedSeparators == DaxSeparators.Eu)
            {
                WriteUsageError(
                    "TOMIX_FORMAT_SEPARATORS_DISPLAY_ONLY",
                    "--separators eu only changes what is shown; models always store DAX with us separators.",
                    "Drop --separators to save, or preview without --save, --save-to, --stage or --revert.",
                    errorFormat);
                return false;
            }

            style = style with { Separators = parsedSeparators };
        }

        if (FormatStyle.TryParseFunctionSpacing(functionSpacing, out var parsedSpacing))
            style = style with { FunctionSpacing = parsedSpacing };
        if (longLines || shortLines)
            style = style with { Long = longLines };
        return true;
    }

    private static void WriteUsageError(string code, string message, string hint, string? errorFormat)
        => ErrorOutput.Write([new TomixDiagnostic(code, DiagnosticSeverity.Error, message, hint)], errorFormat);

    /// <summary>
    /// Whether piped stdin can be the expression to format: only when nothing names a model to
    /// format instead. A model, <c>--path</c>, or a write option (<c>--save</c>, <c>--save-to</c>,
    /// <c>--stage</c>, <c>--revert</c>) means the model, so stdin is left alone; <c>-e -</c> still
    /// reads it.
    /// </summary>
    internal static bool ReadsPipedExpression(string? model, string? path, bool writes)
        => string.IsNullOrWhiteSpace(model) && string.IsNullOrWhiteSpace(path) && !writes;

    // The user typed this source, so show where it breaks. Only the first error: the parser stops there.
    private static void WriteInlineCaret(string expression, IReadOnlyList<TomixDiagnostic> diagnostics)
    {
        if (diagnostics.SelectMany(d => d.SyntaxErrors ?? []).FirstOrDefault() is { } error)
            SyntaxErrorCaret.Write(expression, error);
    }

    // A sweep failure must not be silent: otherwise an HTTP error from the M formatter reports
    // only "Failed: N" and the user has to re-run inline to learn why. A uniform failure prints
    // once with the affected-object count; distinct failures print once per object.
    private static void WriteFailureDetails(IReadOnlyList<ModelFormatObjectResult> results)
    {
        var failures = results
            .Where(r => r.Status == "failed" && !string.IsNullOrWhiteSpace(r.Error?.Message))
            .ToList();

        foreach (var group in failures.GroupBy(r => r.Error!.Message))
        {
            var first = group.First();
            var label = first.Measure is null
                ? $"{first.Table}/{first.Partition}"
                : $"{first.Table}/{first.Measure}";
            var more = group.Count() - 1;
            var suffix = more > 0 ? $" (+{more} more)" : "";
            Console.Error.WriteLine($"{label}{suffix}: {group.Key}");
        }
    }

    // The formatter only speaks DAX and M.
    private static ExpressionLanguage HighlightLanguage(string language)
        => FormatterLanguages.IsDax(language) ? ExpressionLanguage.Dax : ExpressionLanguage.M;

    internal static void Render(IFormatModelResult result, bool check = false)
    {
        switch (result)
        {
            // A check changed nothing, so it names what --save would format instead of counting it.
            case ModelFormatResult model when check:
                AnsiConsole.MarkupLine(Styling.Success($"Already formatted: {model.Unchanged}"));
                AnsiConsole.MarkupLine(Styling.Warning($"Not formatted: {model.Formatted}"));
                foreach (var row in model.Results.Where(r => r.Status == "formatted"))
                    AnsiConsole.MarkupLine($"  {Markup.Escape($"{row.Table}/{row.Measure ?? row.Partition}")}");
                AnsiConsole.MarkupLine(Styling.Error($"Failed: {model.Failed}"));
                WriteFailureDetails(model.Results);
                break;

            // A single-expression check answers with its exit code alone; the formatted text is
            // what a run without --check prints.
            case InlineFormatResult or ObjectFormatResult when check:
                break;

            case InlineFormatResult inline:
                AnsiConsole.MarkupLine(Styling.ExpressionMarkup(
                    HighlightLanguage(inline.Language), inline.Formatted));
                break;

            case ObjectFormatResult obj:
                AnsiConsole.MarkupLine(Styling.ExpressionMarkup(
                    HighlightLanguage(obj.Language), obj.Formatted));
                if (obj.Saved)
                    MutationOutput.RenderSaved(obj.Outcome);
                MutationOutput.RenderSync(obj.Outcome);
                break;

            case ModelFormatResult model:
                // A failure applies nothing, so the formatted count is only what would have changed.
                var notApplied = model.Failed > 0 && model.Formatted > 0 ? " (not applied)" : "";
                AnsiConsole.MarkupLine(Styling.Success($"Formatted: {model.Formatted}{notApplied}"));
                AnsiConsole.MarkupLine(Styling.Warning($"Unchanged: {model.Unchanged}"));
                AnsiConsole.MarkupLine(Styling.Error($"Failed: {model.Failed}"));
                WriteFailureDetails(model.Results);

                if (model.Saved)
                    MutationOutput.RenderSaved(model.Outcome);
                else if (model.Status == MutationStatus.Staged)
                    AnsiConsole.MarkupLine(Styling.Success("Mutation staged."));
                else if (model.Formatted > 0 && model.Failed == 0)
                    AnsiConsole.MarkupLine(Styling.Muted("Not saved — re-run with --save to persist or --stage to stage."));

                MutationOutput.RenderSync(model.Outcome);
                break;
        }
    }
}
