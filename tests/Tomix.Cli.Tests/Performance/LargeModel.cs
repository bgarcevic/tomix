using Microsoft.AnalysisServices.Tabular;

namespace Tomix.Cli.Tests.Performance;

/// <summary>
/// The large model of the live-session benchmark (#352, ADR 0004): 100 tables of 30 columns and
/// 10 measures each (3,000 columns, 1,000 measures), chained by 99 relationships. Half the
/// measures aggregate a column and half reference other measures, so dependencies, DAX checks and
/// BPA have real work. Deterministic, so runs compare.
/// </summary>
internal static class LargeModel
{
    public const int Tables = 100;
    public const int ColumnsPerTable = 30;
    public const int MeasuresPerTable = 10;

    public static string TableName(int table) => $"Table {table:D3}";

    public static string MeasureName(int table, int measure) => $"Measure {table:D3}.{measure:D2}";

    /// <summary>Writes the model as a TMDL folder at <paramref name="folder"/>.</summary>
    public static void WriteTmdl(string folder)
    {
        var database = new Database { Name = "Large", CompatibilityLevel = 1600, Model = new Model { Name = "Model" } };
        for (var t = 0; t < Tables; t++)
            database.Model.Tables.Add(NewTable(t));

        for (var t = 1; t < Tables; t++)
        {
            database.Model.Relationships.Add(new SingleColumnRelationship
            {
                Name = $"Rel {t:D3}",
                FromColumn = database.Model.Tables[TableName(t)].Columns["ParentKey"],
                ToColumn = database.Model.Tables[TableName(t - 1)].Columns["Key"],
                FromCardinality = RelationshipEndCardinality.Many,
                ToCardinality = RelationshipEndCardinality.One
            });
        }

        TmdlSerializer.SerializeDatabaseToFolder(database, folder);
    }

    private static Table NewTable(int t)
    {
        var name = TableName(t);
        var table = new Table { Name = name };
        table.Columns.Add(new DataColumn { Name = "Key", DataType = DataType.Int64, SourceColumn = "Key" });
        table.Columns.Add(new DataColumn { Name = "ParentKey", DataType = DataType.Int64, SourceColumn = "ParentKey" });
        for (var c = 2; c < ColumnsPerTable; c++)
        {
            var column = $"Value {c:D2}";
            table.Columns.Add(new DataColumn
            {
                Name = column,
                DataType = c % 3 == 0 ? DataType.String : DataType.Decimal,
                SourceColumn = column
            });
        }

        // The first half aggregate a decimal column; the second half build on the first half.
        var half = MeasuresPerTable / 2;
        for (var m = 0; m < MeasuresPerTable; m++)
        {
            var expression = m < half
                ? $"SUM('{name}'[Value {DecimalColumn(m):D2}])"
                : $"DIVIDE([{MeasureName(t, m - half)}], [{MeasureName(t, (m - half + 1) % half)}]) * 2";
            table.Measures.Add(new Measure { Name = MeasureName(t, m), Expression = expression });
        }

        table.Partitions.Add(new Partition
        {
            Name = name,
            Mode = ModeType.Import,
            Source = new MPartitionSource { Expression = "let\n    Source = #table({}, {})\nin\n    Source" }
        });
        return table;
    }

    /// <summary>The <paramref name="index"/>th decimal column: columns 2 onward that are not a multiple of 3.</summary>
    private static int DecimalColumn(int index)
    {
        var column = 2;
        for (var seen = 0; ; column++)
        {
            if (column % 3 == 0)
                continue;
            if (seen++ == index)
                return column;
        }
    }
}
