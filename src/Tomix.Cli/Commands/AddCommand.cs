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
            Description = "Object path of the new object. Slash-separated: 'Sales/Revenue', 'Products', 'Admin'. DAX forms also accepted: \"'Sales'[Revenue]\". Pair with -t. Relationships use 'Sales[Key]->Product[Key]' (many side -> one side)."
        };
        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to model (if not using --model)",
            Arity = ArgumentArity.ZeroOrOne
        };
        var typeOption = new Option<string?>("--type")
        {
            Description = $"Object type. Known values: {ModelObjectTypeCatalog.CreationListText}. " +
                          "Data sources always require -t (no path keyword infers them)."
        };
        typeOption.Aliases.Add("-t");
        var valueOption = new Option<string[]?>("-i")
        {
            Description = "Compatibility form of --expression / --set: an unpaired -i is the new object's value; pair each -q with a following -i to set a property. Use '-' to read from stdin.",
            Arity = ArgumentArity.ZeroOrMore
        };
        var queryOption = new Option<string[]?>("-q")
        {
            Description = "Compatibility form of --set: property name to set on the newly-created object; pair each -q with a following -i value. Repeatable.",
            Arity = ArgumentArity.ZeroOrMore
        };
        var expressionOption = new Option<string?>("--expression")
        {
            Description = "Expression or value for the new object. Use '-' to read from stdin."
        };
        expressionOption.Aliases.Add("-e");
        var setOption = new Option<string[]?>("--set")
        {
            Description = "Set a property on the newly-created object as name=value, e.g. --set formatString=\"#,0\". Repeatable. Names accept dotted paths, bracket indexers, and DisplayName matching.",
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
            Description = "Read expression from file"
        };
        var ifNotExistsOption = new Option<bool>("--if-not-exists")
        {
            Description = "Succeed silently if the object already exists (exit 0)"
        };
        var forceOption = LifecycleOptions.Force();
        var overwriteOption = LifecycleOptions.Overwrite();
        var dryRunOption = LifecycleOptions.DryRun();
        var saveToOption = LifecycleOptions.SaveTo();
        var serializationOption = LifecycleOptions.Serialization();
        var saveOption = LifecycleOptions.Save();
        var stageOption = LifecycleOptions.Stage();
        var revertOption = LifecycleOptions.Revert();
        var noSyncOption = LifecycleOptions.NoSync();

        var modeOption = new Option<string?>("--mode")
        {
            Description = "Partition storage mode: Import, DirectQuery, Dual, DirectLake, Push, Default."
        };
        modeOption.AcceptAmongIgnoreCase("Import", "DirectQuery", "Dual", "DirectLake", "Push", "Default");
        var sourceOption = new Option<string?>("--source")
        {
            Description = "Provider name for a ProviderDataSource (e.g. System.Data.SqlClient)."
        };
        var endpointOption = new Option<string?>("--endpoint")
        {
            Description = "Server/endpoint address for a data source connection."
        };
        var connectionStringOption = new Option<string?>("--connection-string")
        {
            Description = "Full connection string for a ProviderDataSource."
        };
        var sourceTableOption = new Option<string?>("--source-table")
        {
            Description = "Source entity/table name for an EntityPartition."
        };
        var sourceDatabaseOption = new Option<string?>("--source-database")
        {
            Description = "Source database name for a data source connection (Provider/Structured data sources)."
        };
        var sourceSchemaOption = new Option<string?>("--source-schema")
        {
            Description = "Source schema name for an EntityPartition."
        };
        var partitionExpressionOption = new Option<string?>("--partition-expression")
        {
            Description = "M/DAX expression for a partition source."
        };
        var columnsOption = new Option<string?>("--columns")
        {
            Description = "Comma-separated column names to create on a new table (Table type only)."
        };
        var sourceTypeOption = new Option<string?>("--source-type")
        {
            Description = "Connection protocol for a StructuredDataSource (e.g. tds)."
        };
        var rangeStartOption = new Option<string?>("--range-start")
        {
            Description = "Refresh-policy range start for a PolicyRangePartition (yyyy-MM-dd)."
        };
        var rangeEndOption = new Option<string?>("--range-end")
        {
            Description = "Refresh-policy range end for a PolicyRangePartition (yyyy-MM-dd)."
        };
        var rangeGranularityOption = new Option<string?>("--range-granularity")
        {
            Description = "Refresh-policy range granularity for a PolicyRangePartition: Day (default), Month, Quarter, Year."
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

        var command = new Command("add", "Add an object to the model")
        {
            pathArgument,
            modelArgument,
            typeOption,
            valueOption,
            queryOption,
            expressionOption,
            setOption,
            fileOption,
            ifNotExistsOption,
            forceOption,
            overwriteOption,
            dryRunOption,
            saveToOption,
            serializationOption,
            saveOption,
            stageOption,
            revertOption,
            noSyncOption
        };

        foreach (var option in extraOptions)
            command.Options.Add(option);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "add", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var file = parseResult.GetValue(fileOption);
            var parsed = ParseInterleavedQi(parseResult);
            var expression = parseResult.GetValue(expressionOption);
            if (expression is not null && parsed.PrimaryValue is not null)
            {
                ErrorOutput.Write(
                    [new TomixDiagnostic(
                        "TOMIX_ADD_INPUT_CONFLICT",
                        DiagnosticSeverity.Error,
                        "Pass either --expression or an unpaired -i, not both.",
                        "Give the new object's value once: --expression <value> or a single -i <value>.")],
                    GlobalOptions.ErrorFormatValue(parseResult, formatValue));
                return 2;
            }

            if (parsed.DanglingProperty is not null)
            {
                // Was AnsiConsole.MarkupLine, which writes to stdout — so `tx add ... | jq` got
                // markup on the data stream instead of an empty one. Errors belong on stderr
                // (docs/cli-ux-guidelines.md), with a code and --error-format support.
                ErrorOutput.Write(
                    [new TomixDiagnostic(
                        "TOMIX_ADD_VALUE_REQUIRED",
                        DiagnosticSeverity.Error,
                        $"Option -q '{parsed.DanglingProperty}' has no matching -i value.",
                        "Pair each -q <property> with a following -i <value>.")],
                    GlobalOptions.ErrorFormatValue(parseResult, formatValue));
                return 2;
            }

            var value = InputValueResolver.Resolve(expression ?? parsed.PrimaryValue, file);
            IReadOnlyList<ModelPropertyAssignment> properties =
            [
                .. parsed.Properties,
                .. ParseSetAssignments(parseResult.GetValue(setOption)),
            ];

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
                        Overwrite: parseResult.GetValue(overwriteOption),
                        DryRun: parseResult.GetValue(dryRunOption)),
                    cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue));

            return CommandOutput.Render(parseResult, result, formatValue, Render);
        });

        return command;
    }

    private static void Render(AddModelObjectResult result)
    {
        if (result.Reverted)
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

        AnsiConsole.MarkupLine(Styling.Success($"Added: {result.Added}"));
        if (result.Staged == true)
            AnsiConsole.MarkupLine(Styling.Guidance("Staged. Run 'tx stage commit' to promote."));
        else if (result.DryRun == true)
            AnsiConsole.MarkupLine(Styling.Guidance("Dry run: nothing was saved."));
        else if (result.Saved is false)
            AnsiConsole.MarkupLine(Styling.Warning("Changes not saved. Use --save to persist or --stage to stage."));
        else
            AnsiConsole.MarkupLine(Styling.Success($"Saved: {result.Saved}"));

        if (result.Synced)
            AnsiConsole.MarkupLine(Styling.Success($"Synced: {Styling.MarkupEscape(result.SyncTarget!)}"));
        else if (result.SyncWarning is not null)
            AnsiConsole.MarkupLine(Styling.Warning(Styling.MarkupEscape(result.SyncWarning)));
    }

    /// <summary>Splits a --set value at the first '='; empty name means it was malformed.</summary>
    internal static string SplitSetName(string raw)
    {
        var eq = raw.IndexOf('=');
        return eq > 0 ? raw[..eq].Trim() : "";
    }

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
            var eq = value.IndexOf('=');
            assignments.Add(new ModelPropertyAssignment(name, value[(eq + 1)..]));
        }

        return assignments;
    }

    internal static (string? PrimaryValue, IReadOnlyList<ModelPropertyAssignment> Properties, string? DanglingProperty) ParseInterleavedQi(
        ParseResult parseResult)
    {
        string? primaryValue = null;
        var properties = new List<ModelPropertyAssignment>();
        string? pendingQuery = null;
        string? dangling = null;

        foreach (var (option, value) in OrderedOptionTokens.ReadOptions(parseResult))
        {
            if (option == "-q")
            {
                dangling ??= pendingQuery;
                pendingQuery = value;
                continue;
            }

            if (option == "-i")
            {
                if (value is null)
                    continue;

                // A value applies to the most recent -q (as that property) or, failing that, as the primary value.
                if (pendingQuery is not null)
                {
                    properties.Add(new ModelPropertyAssignment(pendingQuery, value));
                    pendingQuery = null;
                }
                else
                {
                    primaryValue ??= value;
                }

                continue;
            }

            // Any other option interrupts a -q that never received its -i value; report it
            // instead of silently dropping the property.
            dangling ??= pendingQuery;
            pendingQuery = null;
        }

        dangling ??= pendingQuery;
        return (primaryValue, properties, dangling);
    }
}
