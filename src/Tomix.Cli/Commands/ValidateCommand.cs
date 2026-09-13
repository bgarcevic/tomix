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
            Description = "Optional path to the model; defaults to the active connection",
            Arity = ArgumentArity.ZeroOrOne
        };
        var ciOption = new Option<string?>("--ci")
        {
            Description = "Print CI log-group commands to stderr for the given system: vsts or github"
        };
        var trxOption = new Option<string?>("--trx")
        {
            Description = "Write results to a .trx test-run file at this path"
        };
        var errorsOnlyOption = new Option<bool>("--errors-only")
        {
            Description = "Only show errors"
        };
        var noWarningsOption = new Option<bool>("--no-warnings")
        {
            Description = "Leave out analyzer warnings"
        };
        var noMultilineOption = new Option<bool>("--no-multiline")
        {
            Description = "Show multi-line cell content on one line. Applies to text output."
        };
        var serverOnlyOption = new Option<bool>("--server-only")
        {
            Description = "Only show errors reported by the connected server"
        };

        var command = new Command("validate", "Check a model's DAX expressions and relationship integrity (--ci for CI output, --trx for test results)")
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
                parseResult,
                result,
                format,
                data => ValidateRenderer.Render(data, errorsOnly, parseResult.GetValue(noMultilineOption), includeBanner: !OutputFormats.IsCsv(format)));
        });

        return command;
    }

}
