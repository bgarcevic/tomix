using System.CommandLine;
using Tomix.App.Get;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

internal sealed class GetCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    private readonly CliStateStore _state;

    public GetCommand(IReadOnlyList<IModelProvider> providers, CliStateStore state)
    {
        _providers = providers;
        _state = state;
    }

    public Command Build()
    {
        var pathArgument = new Argument<string>("path")
        {
            Description = "The object to read, slash-separated: 'Sales', 'Sales/Amount'. DAX form works too."
        };

        var modelArgument = new Argument<string>("model")
        {
            Description = "Optional path to the model; defaults to the active connection",
            Arity = ArgumentArity.ZeroOrOne
        };

        var queryOption = new Option<string?>("--query")
        {
            Description = "Read just one property (for example --query expression)"
        };


        var typeOption = new Option<string?>("--type")
        {
            Description = "Type to pick when the path matches several objects under a table."
        };
        typeOption.Aliases.Add("-t");

        var command = new Command("get", "Read a model object's properties")
        {
            pathArgument,
            modelArgument,
            queryOption,
            typeOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var path = parseResult.GetValue(pathArgument) ?? "";
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            var errorFormat = GlobalOptions.ErrorFormatValue(parseResult, formatValue);
            if (QuietCollisionGuard.TryReject(parseResult))
                return 2;
            var query = parseResult.GetValue(queryOption);
            var typeValue = parseResult.GetValue(typeOption);

            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "get", OutputFormats.Text, OutputFormats.Json, OutputFormats.Csv, OutputFormats.Tmdl, OutputFormats.Bim, OutputFormats.Tmsl))
                return 2;

            ModelObjectKind? type = null;
            if (!string.IsNullOrWhiteSpace(typeValue))
            {
                if (!ModelObjectKindParser.TryParse(typeValue, out var parsed))
                {
                    return TypeValidation.WriteInvalidTypeError(GlobalOptions.ErrorFormatValue(parseResult, formatValue));
                }

                type = parsed;
            }

            if (!RecentConnections.TryResolveModel(
                    parseResult,
                    GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                    _state,
                    out var reference,
                    out var recentExit))
                return recentExit;
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            var result = await CliSpinner.RunAsync(
                "Loading model...",
                () => new GetModelHandler(_providers).HandleAsync(
                    new GetModelRequest(
                        reference,
                        path, query, type),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue) || OutputFormats.IsCsv(formatValue));

            return CommandOutput.Render(
                result,
                formatValue,
                data => GetRenderer.Render(data, formatValue),
                GetRenderer.ToReferenceJson,
                renderCsv: GetRenderer.RenderCsv,
                errorFormat: errorFormat);
        });

        return command;
    }

}
