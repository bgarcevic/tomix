using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Column property coverage for set: the full writable scalar surface (data type, summarization,
/// key/nullability flags, lineage tags, detail-row attributes) plus the sort-by-column reference,
/// with value parsing and the errors for values that cannot apply.
/// </summary>
public sealed class TomColumnPropertyTests
{
    [Fact]
    public void SetProperty_DataType_AcceptsEnumName()
    {
        var (mutator, table) = NewModel();

        mutator.SetProperty(Set("T/C", "datatype", "String"));

        Assert.Equal(DataType.String, table.Columns["C"].DataType);
    }

    [Fact]
    public void SetProperty_DataType_AcceptsFriendlyAlias()
    {
        var (mutator, table) = NewModel();

        mutator.SetProperty(Set("T/C", "dataType", "bool"));

        Assert.Equal(DataType.Boolean, table.Columns["C"].DataType);
    }

    [Fact]
    public void SetProperty_DataType_InvalidValue_ListsValidNames()
    {
        var (mutator, _) = NewModel();

        var ex = Assert.Throws<ArgumentException>(() => mutator.SetProperty(Set("T/C", "dataType", "Number")));

        Assert.Contains("Int64", ex.Message);
        Assert.Contains("dataType", ex.Message);
    }

    [Theory]
    [InlineData("summarizeBy", "Sum")]
    [InlineData("summarize by", "None")]
    public void SetProperty_SummarizeBy_ParsesAggregateFunction(string property, string value)
    {
        var (mutator, table) = NewModel();

        mutator.SetProperty(Set("T/C", property, value));

        Assert.Equal(Enum.Parse<AggregateFunction>(value), table.Columns["C"].SummarizeBy);
    }

    [Fact]
    public void SetProperty_EncodingHintAndAlignment_ParseEnums()
    {
        var (mutator, table) = NewModel();

        mutator.SetProperty(Set("T/C", "encodingHint", "Value"));
        mutator.SetProperty(Set("T/C", "alignment", "Left"));

        Assert.Equal(EncodingHintType.Value, table.Columns["C"].EncodingHint);
        Assert.Equal(Alignment.Left, table.Columns["C"].Alignment);
    }

    [Fact]
    public void SetProperty_BooleanFlags_Apply()
    {
        var (mutator, table) = NewModel();
        var column = (DataColumn)table.Columns["C"];

        mutator.SetProperty(Set("T/C", "isKey", "true"));
        mutator.SetProperty(Set("T/C", "isNullable", "false"));
        mutator.SetProperty(Set("T/C", "isUnique", "true"));
        mutator.SetProperty(Set("T/C", "isAvailableInMDX", "false"));
        mutator.SetProperty(Set("T/C", "keepUniqueRows", "true"));

        Assert.True(column.IsKey);
        Assert.False(column.IsNullable);
        Assert.True(column.IsUnique);
        Assert.False(column.IsAvailableInMDX);
        Assert.True(column.KeepUniqueRows);
    }

    [Fact]
    public void SetProperty_IntProperties_ParseAndReject()
    {
        var (mutator, table) = NewModel();

        mutator.SetProperty(Set("T/C", "tableDetailPosition", "3"));
        mutator.SetProperty(Set("T/C", "displayOrdinal", "7"));

        Assert.Equal(3, table.Columns["C"].TableDetailPosition);
        Assert.Equal(7, table.Columns["C"].DisplayOrdinal);

        var ex = Assert.Throws<ArgumentException>(() => mutator.SetProperty(Set("T/C", "displayOrdinal", "first")));
        Assert.Contains("integer", ex.Message);
    }

    [Fact]
    public void SetProperty_StringProperties_Apply()
    {
        var (mutator, table) = NewModel();
        var column = (DataColumn)table.Columns["C"];

        mutator.SetProperty(Set("T/C", "dataCategory", "Time"));
        mutator.SetProperty(Set("T/C", "lineageTag", "tag-1"));
        mutator.SetProperty(Set("T/C", "sourceLineageTag", "src-tag-1"));
        mutator.SetProperty(Set("T/C", "sourceProviderType", "int"));
        mutator.SetProperty(Set("T/C", "sourceColumn", "c_src"));

        Assert.Equal("Time", column.DataCategory);
        Assert.Equal("tag-1", column.LineageTag);
        Assert.Equal("src-tag-1", column.SourceLineageTag);
        Assert.Equal("int", column.SourceProviderType);
        Assert.Equal("c_src", column.SourceColumn);
    }

    [Fact]
    public void SetProperty_SourceColumn_OnCalculatedTableColumn_ReadsBackFromSnapshot()
    {
        var (mutator, table) = NewModel();
        table.Columns.Add(new CalculatedTableColumn { Name = "CTC", SourceColumn = "Orig", DataType = DataType.String });

        mutator.SetProperty(Set("T/CTC", "sourceColumn", "Renamed"));

        var snapshot = TomModelSummarizer.Snapshot((Database)table.Model.Database, "M");
        var column = snapshot.Objects.Single(o => o.Name == "T").Children.Single(c => c.Name == "CTC");
        Assert.Equal("Renamed", column.SourceColumn);
    }

    [Fact]
    public void SetProperty_SourceColumn_OnCalculatedColumn_PointsToExpression()
    {
        var (mutator, table) = NewModel();
        table.Columns.Add(new CalculatedColumn { Name = "Calc", Expression = "1" });

        var ex = Assert.Throws<NotSupportedException>(() => mutator.SetProperty(Set("T/Calc", "sourceColumn", "x")));

        Assert.Contains("calculated columns", ex.Message);
        Assert.Contains("expression", ex.Message);
    }

    [Fact]
    public void SetProperty_SortByColumn_ResolvesSibling()
    {
        var (mutator, table) = NewModel();

        mutator.SetProperty(Set("T/C", "sortByColumn", "C2"));

        Assert.Same(table.Columns["C2"], table.Columns["C"].SortByColumn);
    }

    [Fact]
    public void SetProperty_SortByColumn_EmptyClears()
    {
        var (mutator, table) = NewModel();
        table.Columns["C"].SortByColumn = table.Columns["C2"];

        mutator.SetProperty(Set("T/C", "sortByColumn", ""));

        Assert.Null(table.Columns["C"].SortByColumn);
    }

    [Fact]
    public void SetProperty_SortByColumn_NotFound_NamesTheTable()
    {
        var (mutator, _) = NewModel();

        var ex = Assert.Throws<ArgumentException>(() => mutator.SetProperty(Set("T/C", "sortByColumn", "Missing")));

        Assert.Contains("'Missing'", ex.Message);
        Assert.Contains("'T'", ex.Message);
    }

    [Fact]
    public void SetProperty_UnknownColumnProperty_HintListsWritableSet()
    {
        var (mutator, _) = NewModel();

        var ex = Assert.Throws<NotSupportedException>(() => mutator.SetProperty(Set("T/C", "bogus", "x")));

        Assert.Contains("summarizeBy", ex.Message);
        Assert.Contains("sortByColumn", ex.Message);
    }

    private static ModelObjectSetRequest Set(string path, string property, string value)
        => new(path, [new ModelPropertyAssignment(property, value)], ModelObjectKind.Column);

    private static (TomModelMutator Mutator, Table Table) NewModel()
    {
        var db = new Database { Name = "M", Model = new Model { Name = "Model" } };
        var table = new Table { Name = "T" };
        table.Partitions.Add(new Partition
        {
            Name = "T",
            Source = new MPartitionSource { Expression = "let x = 1 in x" }
        });
        table.Columns.Add(new DataColumn { Name = "C", DataType = DataType.Int64 });
        table.Columns.Add(new DataColumn { Name = "C2", DataType = DataType.String });
        db.Model.Tables.Add(table);
        return (new TomModelMutator(db), table);
    }
}
