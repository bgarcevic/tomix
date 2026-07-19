using System.CommandLine;
using Spectre.Console;
using Tomix.App.State;
using Tomix.App.Validate;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

internal sealed class ValidateCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    private readonly CliStateStore _state;

    public ValidateCommand(IReadOnlyList<IModelProvider> providers, CliStateStore state)
    {
        _providers = providers;
        _state = state;
    }

    public Command Build()
    {
        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to model (if not using --model)",
            Arity = ArgumentArity.ZeroOrOne
        };
        var ciOption = new Option<string?>("--ci")
        {
            Description = "Emit CI logging commands to stderr: vsts or github"
        };
        var trxOption = new Option<string?>("--trx")
        {
            Description = "Write results as a VSTEST .trx file to the specified path"
        };
        var errorsOnlyOption = new Option<bool>("--errors-only")
        {
            Description = "Only show errors"
        };
        var noWarningsOption = new Option<bool>("--no-warnings")
        {
            Description = "Hide warnings from the semantic analyzer"
        };
        var noMultilineOption = new Option<bool>("--no-multiline")
        {
            Description = "Collapse multi-line cell content to a single line. Text output only."
        };
        var serverOnlyOption = new Option<bool>("--server-only")
        {
            Description = "Only show errors reported by the connected server"
        };

        var command = new Command("validate", "Validate DAX expressions and relationship integrity (--ci for CI output, --trx for VSTEST)")
        {
            modelArgument,
            ciOption,
            trxOption,
            errorsOnlyOption,
            noWarningsOption,
            noMultilineOption,
            serverOnlyOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            if (!CommandOutput.TryValidateFormat(parseResult, format, "validate", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var errorsOnly = parseResult.GetValue(errorsOnlyOption);

            if (!RecentConnections.TryResolveModel(
                    parseResult,
                    GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                    _state,
                    out var model,
                    out var recentExit))
                return recentExit;

            var result = await CliSpinner.RunAsync(
                "Validating model...",
                () => new ValidateModelHandler(_providers).HandleAsync(
                    new ValidateModelRequest(
                        model,
                        errorsOnly,
                        parseResult.GetValue(noWarningsOption) || errorsOnly,
                        parseResult.GetValue(serverOnlyOption)),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(format) || OutputFormats.IsCsv(format));

            if (result.Data is not null)
            {
                var trx = parseResult.GetValue(trxOption);
                if (!string.IsNullOrWhiteSpace(trx))
                    TrxWriter.Write(trx, "tx validate", ValidateRenderer.ToTrxTests(result.Data));

                ValidateRenderer.EmitCi(parseResult.GetValue(ciOption), result.Data);
            }

            return CommandOutput.Render(
                result,
                format,
                data => ValidateRenderer.Render(data, errorsOnly, parseResult.GetValue(noMultilineOption), includeBanner: !OutputFormats.IsCsv(format)));
        });

        return command;
    }

}
