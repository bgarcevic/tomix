using System.CommandLine;
using Tomix.App.State;
using Tomix.App.Summary;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

internal sealed class SummaryCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    private readonly CliStateStore _state;

    public SummaryCommand(IReadOnlyList<IModelProvider> providers, CliStateStore state)
    {
        _providers = providers;
        _state = state;
    }

    public Command Build()
    {
        var modelArgument = new Argument<string>("model")
        {
            Description = "Model path, Fabric path, or omit to use the active connection",
            Arity = ArgumentArity.ZeroOrOne
        };

        var command = new Command("summary", "Show where a model lives and what it contains")
        {
            modelArgument
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            if (!RecentConnections.TryResolveModel(
                    parseResult,
                    GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                    _state,
                    out var reference,
                    out var recentExit))
                return recentExit;
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            var errorFormat = GlobalOptions.ErrorFormatValue(parseResult, formatValue);

            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "summary", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var result = await CliSpinner.RunAsync(
                "Loading model...",
                () => new SummaryModelHandler(_providers).HandleAsync(
                    new SummaryModelRequest(reference),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue));

            return CommandOutput.Render(
                result,
                formatValue,
                data =>
                {
                    SummaryRenderer.Render(data);
                    if (!quiet)
                        SummaryRenderer.RenderHint();
                },
                data => data,
                renderCsv: null,
                errorFormat: errorFormat);
        });

        return command;
    }
}
