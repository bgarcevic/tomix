using System.CommandLine;
using Tomix.App;
using Tomix.App.Get;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

internal sealed class GetCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    private readonly AppServices _services;

    public GetCommand(IReadOnlyList<IModelProvider> providers, AppServices services)
    {
        _providers = providers;
        _services = services;
    }

    public Command Build()
    {
        var pathArgument = new Argument<string>("path")
        {
            Description = "Object path. Slash-separated: 'Sales', 'Sales/Amount'. DAX forms also accepted."
        };

        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to model (if not using --model)",
            Arity = ArgumentArity.ZeroOrOne
        };

        var queryOption = new Option<string?>("--query")
        {
            Description = "Query a specific property (e.g., -q expression, -q formatString)"
        };
        queryOption.Aliases.Add("-q");

        var typeOption = new Option<string?>("--type")
        {
            Description = "Disambiguate when the path matches multiple table-children."
        };
        typeOption.Aliases.Add("-t");

        var command = new Command("get", "Get properties of a model object")
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
            var errorFormat = parseResult.GetValue(GlobalOptions.ErrorFormat);
            var query = parseResult.GetValue(queryOption);
            var typeValue = parseResult.GetValue(typeOption);

            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "get", OutputFormats.Text, OutputFormats.Json, OutputFormats.Csv, OutputFormats.Tmdl, OutputFormats.Bim, OutputFormats.Tmsl))
                return 2;

            ModelObjectKind? type = null;
            if (!string.IsNullOrWhiteSpace(typeValue))
            {
                if (!ModelObjectKindParser.TryParse(typeValue, out var parsed))
                {
                    return TypeValidation.WriteInvalidTypeError();
                }

                type = parsed;
            }

            if (!RecentConnections.TryResolveModel(
                    parseResult,
                    GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                    _services.State,
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
