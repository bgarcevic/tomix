using Microsoft.AnalysisServices.Tabular;
using Mdl.Core.Models;

namespace Mdl.Provider.Tom;

public static class TomModelSummarizer
{
    public static ModelSummary Summarize(Database database, string name)
    {
        var model = database.Model;
        return new ModelSummary(
            Name: name,
            CompatibilityLevel: database.CompatibilityLevel,
            Tables: model.Tables.Count,
            Columns: model.Tables.Sum(t => t.Columns.Count),
            Measures: model.Tables.Sum(t => t.Measures.Count),
            Relationships: model.Relationships.Count,
            Roles: model.Roles.Count);
    }

    public static ModelInventory Inventory(Database database, string name)
    {
        var model = database.Model;
        var tables = model.Tables
            .Select(t => new ModelTableInfo(
                Name: t.Name,
                Columns: t.Columns.Count,
                Measures: t.Measures.Count,
                Hidden: t.IsHidden,
                Calculated: t.Partitions.Any(p => p.SourceType == PartitionSourceType.Calculated)))
            .ToList();

        return new ModelInventory(
            Name: name,
            CompatibilityLevel: database.CompatibilityLevel,
            Tables: tables.Count,
            Columns: tables.Sum(t => t.Columns),
            Measures: tables.Sum(t => t.Measures),
            Relationships: model.Relationships.Count,
            Roles: model.Roles.Count,
            CalculationGroups: model.Tables.Count(t => t.CalculationGroup is not null),
            TableDetails: tables);
    }
}
