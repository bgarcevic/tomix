using System.CommandLine;
using Spectre.Console;
using Tomix.App.Add;
using Tomix.App.Mutations;
using Tomix.App.State;
using Tomix.Cli.Output;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

internal sealed class AddCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    private readonly CliStateStore _state;
    private readonly MutationStores _mutations;

    public AddCommand(IReadOnlyList<IModelProvider> providers, CliStateStore state, MutationStores mutations)
    {
        _providers = providers;
        _state = state;
        _mutations = mutations;
    }

    public Command Build()
    {
        var pathArgument = new Argument<string>("path")
        {
            Description = "Where to create the object, slash-separated: 'Sales/Revenue', 'Products', 'Admin'. DAX form also accepted: \"'Sales'[Revenue]\". Combine with -t. For relationships, use 'Sales[Key]->Product[Key]' (many side first)."
        };
        var modelArgument = new Argument<string>("model")
        {
            Description = "Optional path to the model; defaults to the active connection",
            Arity = ArgumentArity.ZeroOrOne
        };
        var typeOption = new Option<string?>("--type")
        {
            Description = "Kind of object to create, for example Measure, Table, CalcColumn, or Relationship; " +
                          "an invalid value lists them all. Data sources always need -t."
        };
        typeOption.Aliases.Add("-t");
        var expressionOption = new Option<string?>("--expression")
        {
            Description = "Expression or value for the new object. Use '-' to read from stdin."
        };
        expressionOption.Aliases.Add("-e");
        var setOption = new Option<string[]?>("--set")
        {
            Description = "Set a property on the new object: name=value, e.g. --set formatString=\"#,0\". Repeatable. Names can use dotted paths, bracket indexers, or a DisplayName.",
            Arity = ArgumentArity.ZeroOrMore
        };
        setOption.Validators.Add(result =>
        {
            foreach (var value in result.GetValueOrDefault<string[]?>() ?? [])
                if (SplitSetName(value).Length == 0)
                    result.AddError($"--set '{value}' must be name=value.");
        });
        var fileOption = new Option<string?>("--file")
        {
            Description = "Read the expression from this file"
        };
        var ifNotExistsOption = new Option<bool>("--if-not-exists")
        {
            Description = "Do nothing and exit 0 when the object already exists"
        };
        var forceOption = LifecycleOptions.Force();
        var overwriteOption = LifecycleOptions.Overwrite();
        var saveToOption = LifecycleOptions.SaveTo();
        var serializationOption = LifecycleOptions.Serialization();
        var saveOption = LifecycleOptions.Save();
        var stageOption = LifecycleOptions.Stage();
        var revertOption = LifecycleOptions.Revert();
        var noSyncOption = LifecycleOptions.NoSync();

        var modeOption = new Option<string?>("--mode")
        {
            Description = "Storage mode for the partition: Import, DirectQuery, Dual, DirectLake, Push, or Default"
        };
        modeOption.AcceptAmongIgnoreCase("Import", "DirectQuery", "Dual", "DirectLake", "Push", "Default");
        var sourceOption = new Option<string?>("--source")
        {
            Description = "Provider name for a ProviderDataSource (e.g. System.Data.SqlClient)"
        };
        var endpointOption = new Option<string?>("--endpoint")
        {
            Description = "Server or endpoint address for a data source connection"
        };
        var connectionStringOption = new Option<string?>("--connection-string")
        {
            Description = "Connection string for a ProviderDataSource"
        };
        var sourceTableOption = new Option<string?>("--source-table")
        {
            Description = "Source entity or table name for an EntityPartition"
        };
        var sourceDatabaseOption = new Option<string?>("--source-database")
        {
            Description = "Source database name for a ProviderDataSource or StructuredDataSource"
        };
        var sourceSchemaOption = new Option<string?>("--source-schema")
        {
            Description = "Source schema name for an EntityPartition"
        };
        var partitionExpressionOption = new Option<string?>("--partition-expression")
        {
            Description = "M or DAX expression that defines the partition's source"
        };
        var columnsOption = new Option<string?>("--columns")
        {
            Description = "Comma-separated column names to create on a new table (Table type only)"
        };
        var sourceTypeOption = new Option<string?>("--source-type")
        {
            Description = "Connection protocol for a StructuredDataSource (e.g. tds)"
        };
        var rangeStartOption = new Option<string?>("--range-start")
        {
            Description = "Refresh-policy range start for a PolicyRangePartition (yyyy-MM-dd)"
        };
        var rangeEndOption = new Option<string?>("--range-end")
        {
            Description = "Refresh-policy range end for a PolicyRangePartition (yyyy-MM-dd)"
        };
        var rangeGranularityOption = new Option<string?>("--range-granularity")
        {
            Description = "Refresh-policy range granularity for a PolicyRangePartition: Day, Month, Quarter, or Year (default: Day)"
        };
        rangeGranularityOption.AcceptAmongIgnoreCase("Day", "Month", "Quarter", "Year");

        var extraOptions = new Option[]
        {
            modeOption,
            sourceOption,
            endpointOption,
            connectionStringOption,
            sourceTableOption,
            sourceDatabaseOption,
            sourceSchemaOption,
            partitionExpressionOption,
            columnsOption,
            sourceTypeOption,
            rangeStartOption,
            rangeEndOption,
            rangeGranularityOption
        };

        var command = new Command("add", "Create a new object in the model")
        {
            pathArgument,
            modelArgument,
            typeOption,
            expressionOption,
            setOption,
            fileOption,
            ifNotExistsOption,
            forceOption,
            overwriteOption,
            saveToOption,
            serializationOption,
            saveOption,
            stageOption,
            revertOption,
            noSyncOption
        };

        foreach (var option in extraOptions)
            command.Options.Add(option.In("Partition and data source options"));

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "add", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var file = parseResult.GetValue(fileOption);
            var value = InputValueResolver.Resolve(parseResult.GetValue(expressionOption), file);
            var properties = ParseSetAssignments(parseResult.GetValue(setOption));

            if (!RecentConnections.TryResolveModel(
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
                () => new AddModelObjectHandler(_providers, _mutations).HandleAsync(
                    new AddModelObjectRequest(
                        reference,
                        parseResult.GetValue(pathArgument) ?? "",
                        parseResult.GetValue(typeOption),
                        value,
                        properties,
                        parseResult.GetValue(ifNotExistsOption),
                        parseResult.GetValue(saveOption),
                        parseResult.GetValue(saveToOption),
                        parseResult.GetValue(serializationOption) ?? "",
                        parseResult.GetValue(forceOption),
                        parseResult.GetValue(stageOption),
                        parseResult.GetValue(revertOption),
                        parseResult.GetValue(noSyncOption),
                        parseResult.GetValue(columnsOption),
                        parseResult.GetValue(modeOption),
                        parseResult.GetValue(sourceOption),
                        parseResult.GetValue(endpointOption),
                        parseResult.GetValue(connectionStringOption),
                        parseResult.GetValue(sourceTableOption),
                        parseResult.GetValue(sourceDatabaseOption),
                        parseResult.GetValue(partitionExpressionOption),
                        parseResult.GetValue(sourceTypeOption),
                        parseResult.GetValue(sourceSchemaOption),
                        parseResult.GetValue(rangeStartOption),
                        parseResult.GetValue(rangeEndOption),
                        parseResult.GetValue(rangeGranularityOption),
                        Overwrite: parseResult.GetValue(overwriteOption)),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue));

            return CommandOutput.Render(parseResult, result, formatValue, Render);
        });

        return command;
    }

    private static void Render(AddModelObjectResult result)
    {
        if (result.Status == MutationStatus.Reverted)
        {
            AnsiConsole.MarkupLine(Styling.Success("Reverted."));
            return;
        }

        if (result.ExistingPath is not null)
        {
            AnsiConsole.MarkupLine(Styling.Success(
                $"Already exists: {Styling.MarkupEscape(result.ExistingPath)} (no changes made)"));
            return;
        }

        AnsiConsole.MarkupLine(Styling.Success($"Added: {result.Path}"));
        MutationOutput.RenderPersistence(result.Outcome);
    }

    /// <summary>Splits a --set value at the first '='; empty name means it was malformed.</summary>
    internal static string SplitSetName(string raw)
    {
        var eq = raw.IndexOf('=');
        return eq > 0 ? raw[..eq].Trim() : "";
    }

    // "name=" is an explicit empty value (it clears or removes the property), so only "-" reads
    // stdin. The implicit read on an empty value would block a script whose stdin is redirected.
    internal static IReadOnlyList<ModelPropertyAssignment> ParseSetAssignments(IEnumerable<string?>? raw)
    {
        if (raw is null)
            return [];

        var assignments = new List<ModelPropertyAssignment>();
        foreach (var value in raw)
        {
            if (value is null)
                continue;
            var name = SplitSetName(value);
            if (name.Length == 0)
                continue; // parse-time validator already rejected it
            var assigned = value[(value.IndexOf('=') + 1)..];
            assignments.Add(new ModelPropertyAssignment(
                name, assigned == "-" ? InputValueResolver.Resolve(assigned) ?? "" : assigned));
        }

        return assignments;
    }
}
