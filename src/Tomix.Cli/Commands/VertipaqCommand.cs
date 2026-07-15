using System.CommandLine;
using Spectre.Console;
using Tomix.App.State;
using Tomix.App.Vertipaq;
using Tomix.Cli.Output;
using Tomix.Core.Models;
using Tomix.Core.Vertipaq;

namespace Tomix.Cli.Commands;

internal sealed class VertipaqCommand : ICommandModule
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly IVertipaqAnalyzer _analyzer;

    public VertipaqCommand(IReadOnlyList<IModelProvider> providers, IVertipaqAnalyzer analyzer)
    {
        _providers = providers;
        _analyzer = analyzer;
    }

    public Command Build()
    {
        var tableArgument = new Argument<string?>("table")
        {
            Description = "Table name to filter the analysis to a single table",
            Arity = ArgumentArity.ZeroOrOne
        };

        var tablesOption = new Option<bool>("--tables") { Description = "Show the tables view" };
        var columnsOption = new Option<bool>("--columns") { Description = "Show the columns view (default)" };
        var relationshipsOption = new Option<bool>("--relationships") { Description = "Show the relationships view" };
        var partitionsOption = new Option<bool>("--partitions") { Description = "Show the partitions view" };
        var allOption = new Option<bool>("--all") { Description = "Show tables, columns, relationships, and partitions" };
        var detailOption = new Option<bool>("--detail")
        {
            Description = "Expanded columns: data/dictionary/hierarchy size breakdown, encoding, segments"
        };
        var fieldsOption = new Option<string?>("--fields")
        {
            Description = "Comma-separated fields to display (e.g. name,card,size,%tbl,%db,bar). " +
                          "Available fields vary by view; requires a single view. Text and csv output only."
        };
        var topOption = new Option<int?>("--top")
        {
            Description = "Limit each view to the N largest rows. Text and csv output only."
        };
        var statsOption = new Option<bool>("--stats")
        {
            Description = "Show a model-level storage summary (combine with view flags for more)"
        };
        var annotateOption = new Option<bool>("--annotate")
        {
            Description = "Write statistics into the model as Vertipaq_* annotations (preview unless --save)"
        };
        var saveOption = new Option<bool>("--save") { Description = "Persist --annotate changes to the model" };
        var exportOption = new Option<string?>("--export")
        {
            Description = "Export statistics to a .vpax file for offline analysis"
        };
        var importOption = new Option<string?>("--import")
        {
            Description = "Analyze a previously exported .vpax file offline (no connection needed)"
        };
        var obfuscateOption = new Option<bool>("--obfuscate")
        {
            Description = "Obfuscate names and expressions in the exported .vpax; writes a private .dict dictionary"
        };

        var command = new Command("vertipaq", "Analyze VertiPaq storage statistics for a semantic model")
        {
            tableArgument,
            tablesOption,
            columnsOption,
            relationshipsOption,
            partitionsOption,
            allOption,
            detailOption,
            fieldsOption,
            topOption,
            statsOption,
            annotateOption,
            saveOption,
            exportOption,
            importOption,
            obfuscateOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var formatValue = GlobalOptions.OutputFormatValue(parseResult);
            var errorFormat = parseResult.GetValue(GlobalOptions.ErrorFormat);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);

            if (!CommandOutput.TryValidateFormat(
                    formatValue, "vertipaq", OutputFormats.Text, OutputFormats.Json, OutputFormats.Csv))
                return 2;

            var options = new VertipaqView.ViewOptions(
                Tables: parseResult.GetValue(tablesOption),
                Columns: parseResult.GetValue(columnsOption),
                Relationships: parseResult.GetValue(relationshipsOption),
                Partitions: parseResult.GetValue(partitionsOption),
                All: parseResult.GetValue(allOption),
                Detail: parseResult.GetValue(detailOption),
                Stats: parseResult.GetValue(statsOption),
                Fields: VertipaqView.ParseFieldList(parseResult.GetValue(fieldsOption)),
                Top: parseResult.GetValue(topOption));

            if (options.Top is { } top && top <= 0)
                return WriteUsageError("--top must be a positive integer.");

            var sections = VertipaqView.ResolveSections(options);

            if (options.Fields is { Count: > 0 })
            {
                if (sections.Count != 1)
                    return WriteUsageError(
                        "--fields applies to a single view; pick one of --tables/--columns/--relationships/--partitions.");

                var unknown = VertipaqView.UnknownTokens(sections[0], options.Fields);
                if (unknown.Count > 0)
                    return WriteUsageError(
                        $"Unknown --fields value(s): {string.Join(", ", unknown)}.",
                        $"Valid fields for this view: {string.Join(", ", VertipaqView.ValidTokens(sections[0]))}");
            }

            if (OutputFormats.IsCsv(formatValue) && sections.Count != 1)
                return WriteUsageError(
                    "csv output supports a single view; pick one of --tables/--columns/--relationships/--partitions.");

            var importPath = parseResult.GetValue(importOption);

            ModelReference reference;
            ModelReference? syncTarget;
            if (string.IsNullOrWhiteSpace(importPath))
            {
                if (!RecentConnections.TryGetSource(
                        parseResult,
                        GlobalOptions.ModelValue(parseResult),
                        out var source,
                        out var recentExit))
                    return recentExit;

                // --server never names a local path, so a bare workspace name is expanded to its
                // XMLA endpoint here (as connect does); otherwise it would be mistaken for a
                // local model definition and rejected as unsupported. Recent entries already store
                // a normalized endpoint, and NormalizeEndpoint is idempotent for those.
                var server = source.Server;
                if (!string.IsNullOrWhiteSpace(server))
                    server = ModelReference.NormalizeEndpoint(server);

                // Seed with the picked --recent entry (if any) so the workspace-primary read side
                // (syncTarget) comes from that entry's mirror, not the active session's.
                var resolver = RecentConnections.CreateResolver(source);
                reference = resolver.ResolveReference(source.Model, source.Database, server);
                syncTarget = resolver.ResolveSyncTarget();
            }
            else
            {
                reference = new ModelReference("");
                syncTarget = null;
            }

            var request = new VertipaqRequest(
                reference,
                syncTarget,
                TableFilter: parseResult.GetValue(tableArgument),
                ImportPath: importPath,
                ExportPath: parseResult.GetValue(exportOption),
                Obfuscate: parseResult.GetValue(obfuscateOption),
                Annotate: parseResult.GetValue(annotateOption),
                Save: parseResult.GetValue(saveOption));

            var spinnerLabel = !string.IsNullOrWhiteSpace(importPath)
                ? "Importing statistics..."
                : !string.IsNullOrWhiteSpace(request.ExportPath)
                    ? "Exporting statistics..."
                    : "Analyzing storage...";

            var handler = new VertipaqHandler(_providers, _analyzer);
            var result = await CliSpinner.RunAsync(
                spinnerLabel,
                () => handler.HandleAsync(request, cancellationToken),
                suppress: quiet || OutputFormats.IsJson(formatValue) || OutputFormats.IsCsv(formatValue));

            return CommandOutput.Render(
                result,
                formatValue,
                data => VertipaqRenderer.Render(data, options),
                data => ToJson(data, options),
                data => RenderCsv(data, options, sections),
                errorFormat: errorFormat);
        });

        return command;
    }

    /// <summary>
    /// Stable contract: sections appear when selected (summary always); <c>--fields</c>,
    /// <c>--top</c>, and the bar are text/csv-only concerns and never shape the JSON.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> ToJson(
        VertipaqResult data, VertipaqView.ViewOptions options)
    {
        var stats = data.Stats;
        var json = new Dictionary<string, object?>
        {
            ["model"] = new Dictionary<string, object?>
            {
                ["name"] = stats.ModelName,
                ["server"] = stats.ServerName,
                ["extractionDate"] = IsoUtc(stats.ExtractionDate),
                ["source"] = data.AnalyzedSource,
                ["usedRemoteFallback"] = data.UsedRemoteFallback
            },
            ["summary"] = new Dictionary<string, object?>
            {
                ["totalSize"] = stats.TotalSize,
                ["tableCount"] = stats.TableCount,
                ["columnCount"] = stats.ColumnCount,
                ["maxRowCount"] = stats.MaxRowCount
            }
        };

        var sections = VertipaqView.ResolveSections(options);

        if (sections.Contains(VertipaqView.Section.Tables))
            json["tables"] = stats.Tables.Select(t => (object?)new Dictionary<string, object?>
            {
                ["name"] = t.TableName,
                ["rowCount"] = t.RowCount,
                ["tableSize"] = t.TableSize,
                ["columnsTotalSize"] = t.ColumnsTotalSize,
                ["columnsDataSize"] = t.ColumnsDataSize,
                ["columnsDictionarySize"] = t.ColumnsDictionarySize,
                ["columnsHierarchiesSize"] = t.ColumnsHierarchiesSize,
                ["relationshipsSize"] = t.RelationshipsSize,
                ["userHierarchiesSize"] = t.UserHierarchiesSize,
                ["percentageDatabase"] = t.PercentageDatabase,
                ["columnCount"] = t.ColumnCount,
                ["partitionCount"] = t.PartitionCount,
                ["segmentCount"] = t.SegmentCount,
                ["isReferenced"] = t.IsReferenced
            }).ToList();

        if (sections.Contains(VertipaqView.Section.Columns))
            json["columns"] = stats.Columns.Select(c => (object?)new Dictionary<string, object?>
            {
                ["table"] = c.TableName,
                ["column"] = c.ColumnName,
                ["cardinality"] = c.Cardinality,
                ["dataType"] = c.DataType,
                ["encoding"] = c.Encoding,
                ["totalSize"] = c.TotalSize,
                ["dataSize"] = c.DataSize,
                ["dictionarySize"] = c.DictionarySize,
                ["hierarchiesSize"] = c.HierarchiesSize,
                ["percentageDatabase"] = c.PercentageDatabase,
                ["percentageTable"] = c.PercentageTable,
                ["selectivity"] = c.Selectivity,
                ["segmentCount"] = c.SegmentCount,
                ["partitionCount"] = c.PartitionCount,
                ["isHidden"] = c.IsHidden,
                ["isReferenced"] = c.IsReferenced,
                ["isRowNumber"] = c.IsRowNumber,
                ["state"] = c.State
            }).ToList();

        if (sections.Contains(VertipaqView.Section.Relationships))
            json["relationships"] = stats.Relationships.Select(r => (object?)new Dictionary<string, object?>
            {
                ["name"] = r.RelationshipName,
                ["fromTable"] = r.FromTable,
                ["toTable"] = r.ToTable,
                ["fromColumn"] = r.FromColumn,
                ["toColumn"] = r.ToColumn,
                ["usedSize"] = r.UsedSize,
                ["fromCardinality"] = r.FromCardinality,
                ["toCardinality"] = r.ToCardinality,
                ["missingKeys"] = r.MissingKeys,
                ["invalidRows"] = r.InvalidRows,
                ["oneToManyRatio"] = r.OneToManyRatio,
                ["isActive"] = r.IsActive,
                ["crossFilteringBehavior"] = r.CrossFilteringBehavior
            }).ToList();

        if (sections.Contains(VertipaqView.Section.Partitions))
            json["partitions"] = stats.Partitions.Select(p => (object?)new Dictionary<string, object?>
            {
                ["table"] = p.TableName,
                ["partition"] = p.PartitionName,
                ["rowCount"] = p.RowCount,
                ["dataSize"] = p.DataSize,
                ["segmentCount"] = p.SegmentCount,
                ["state"] = p.State,
                ["type"] = p.Type,
                ["mode"] = p.Mode,
                ["refreshedTime"] = IsoUtc(p.RefreshedTime)
            }).ToList();

        if (data.ExportedPath is not null)
            json["export"] = new Dictionary<string, object?>
            {
                ["path"] = data.ExportedPath,
                ["dictionaryPath"] = data.ObfuscationDictionaryPath
            };

        if (data.Annotate is { } annotate)
            json["annotate"] = new Dictionary<string, object?>
            {
                ["objects"] = annotate.AnnotatedObjects,
                ["skipped"] = annotate.SkippedObjects,
                ["saved"] = annotate.Saved,
                ["synced"] = annotate.Synced,
                ["syncTarget"] = annotate.SyncTarget,
                ["syncWarning"] = annotate.SyncWarning
            };

        return json;
    }

    /// <summary>Single-section CSV (validated up front): resolved fields minus the bar, raw values.</summary>
    private static void RenderCsv(
        VertipaqResult data,
        VertipaqView.ViewOptions options,
        IReadOnlyList<VertipaqView.Section> sections)
    {
        // Re-resolve with the bar dropped so every remaining cell is a raw value.
        var fields = VertipaqView
            .ResolveFields(sections[0], options.Detail, options.Fields is { Count: > 0 } ? options.Fields : null)
            .Where(f => f.Kind != VertipaqView.FieldKind.Bar)
            .ToList();

        var section = VertipaqView
            .BuildSections(data.Stats, options with { Fields = fields.Select(f => f.Token).ToList() })
            .Single();

        CsvOutput.Write(
            fields.Select(f => f.Header).ToArray(),
            section.Rows);
    }

    private static string? IsoUtc(DateTimeOffset? value)
        => value?.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static int WriteUsageError(string message, string? guidance = null)
    {
        var err = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
        err.MarkupLine(Styling.Error(message));
        if (guidance is not null)
            err.MarkupLine(Styling.Guidance($"  → {guidance}"));
        return 2;
    }
}
