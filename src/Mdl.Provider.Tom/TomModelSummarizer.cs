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
}
