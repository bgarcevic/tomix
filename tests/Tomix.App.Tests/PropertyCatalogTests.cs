using Tomix.Core.Models;
using Tomix.Core.Properties;

namespace Tomix.App.Tests;

public sealed class PropertyCatalogTests
{
    private static readonly ModelObjectKind[] AllKinds = Enum.GetValues<ModelObjectKind>();

    [Fact]
    public void For_EveryKind_JsonKeysAreUniqueAndCamelCase()
    {
        foreach (var kind in AllKinds)
        {
            var keys = ModelPropertyCatalog.For(kind).Select(d => d.JsonKey).ToList();
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(keys, k =>
            {
                Assert.True(char.IsLower(k[0]), $"'{k}' must be camelCase");
                Assert.DoesNotContain(' ', k);
            });
        }
    }

    [Fact]
    public void For_EveryKind_HeadersAreUnique()
    {
        foreach (var kind in AllKinds)
        {
            var headers = ModelPropertyCatalog.For(kind).Select(d => d.Header).ToList();
            Assert.Equal(headers.Count, headers.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Fact]
    public void Project_ToleratesNullAndEmptyPropertyBags()
    {
        foreach (var kind in AllKinds)
            foreach (var bag in new IReadOnlyDictionary<string, string>?[] { null, new Dictionary<string, string>() })
            {
                var obj = Leaf(kind) with { Properties = bag };
                foreach (var (key, value) in ModelPropertyCatalog.Project(obj))
                {
                    Assert.True(value is "" or false or 0 || key == "name",
                        $"{kind}.{key} projected {value ?? "null"} from an absent bag; expected \"\"/false/0");
                }
            }
    }

    [Fact]
    public void Project_PreservesDescriptorOrder()
    {
        var obj = Leaf(ModelObjectKind.Measure);
        Assert.Equal(
            ModelPropertyCatalog.For(ModelObjectKind.Measure).Select(d => d.JsonKey),
            ModelPropertyCatalog.Project(obj).Keys);
    }

    [Theory]
    [InlineData(ModelObjectKind.Table,
        "name,description,isHidden,dataCategory,lineageTag,columns,measures,hierarchies,partitions,refreshPolicy,defaultDetailRowsExpression")]
    [InlineData(ModelObjectKind.Measure,
        "name,description,isHidden,expression,formatString,displayFolder,dataType,detailRowsExpression,formatStringExpression,kpi,lineageTag")]
    [InlineData(ModelObjectKind.Column,
        "name,description,sourceColumn,expression,dataType,isHidden,formatString,displayFolder,sortByColumn,summarizeBy,lineageTag")]
    [InlineData(ModelObjectKind.Partition,
        "name,description,expression,mode,dataView,queryGroup")]
    [InlineData(ModelObjectKind.Hierarchy,
        "name,description,isHidden,detail,expression")]
    public void For_PinsThePropertyContractPerKind(ModelObjectKind kind, string expectedKeys)
    {
        Assert.Equal(expectedKeys.Split(','), ModelPropertyCatalog.For(kind).Select(d => d.JsonKey));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("Unknown", "")]
    [InlineData("unknown", "")]
    [InlineData("int64", "Int64")]
    [InlineData("Int64", "Int64")]
    [InlineData("DECIMAL", "Decimal")]
    [InlineData("bool", "Boolean")]
    [InlineData("dateTime", "DateTime")]
    [InlineData("Variant", "Variant")]
    public void NormalizeDataType_CanonicalizesKnownNames(string? raw, string expected)
    {
        Assert.Equal(expected, ModelPropertyCatalog.NormalizeDataType(raw));
    }

    [Fact]
    public void Project_ReadsBagValuesAndCounts()
    {
        var column = Leaf(ModelObjectKind.Column) with
        {
            Name = "Amount",
            SourceColumn = "amount_src",
            Properties = new Dictionary<string, string>
            {
                [PropertyBagKeys.DataType] = "int64",
                [PropertyBagKeys.FormatString] = "#,0",
                [PropertyBagKeys.SortByColumn] = "SortKey"
            }
        };
        var table = Leaf(ModelObjectKind.Table) with { Name = "Sales", Children = [column] };

        var projected = ModelPropertyCatalog.Project(column);
        Assert.Equal("Int64", projected["dataType"]);
        Assert.Equal("#,0", projected["formatString"]);
        Assert.Equal("SortKey", projected["sortByColumn"]);
        Assert.Equal("", projected["displayFolder"]);
        Assert.Equal("amount_src", projected["sourceColumn"]);

        var tableProjected = ModelPropertyCatalog.Project(table);
        Assert.Equal(1, tableProjected["columns"]);
        Assert.Equal(0, tableProjected["measures"]);
    }

    [Fact]
    public void WritableTokens_CoverTheCatalogedKindsOnly()
    {
        Assert.Equal(["name", "description", "isHidden", "dataCategory"],
            ModelPropertyCatalog.WritableTokens(ModelObjectKind.Table));
        Assert.Equal(["name", "description", "isHidden", "expression", "formatString", "displayFolder"],
            ModelPropertyCatalog.WritableTokens(ModelObjectKind.Measure));
        Assert.Equal(["name", "description", "isHidden", "formatString", "displayFolder"],
            ModelPropertyCatalog.WritableTokens(ModelObjectKind.Column));
        Assert.Equal(["name", "expression"],
            ModelPropertyCatalog.WritableTokens(ModelObjectKind.Partition));
        Assert.Empty(ModelPropertyCatalog.WritableTokens(ModelObjectKind.Role));
    }

    private static ModelObject Leaf(ModelObjectKind kind)
        => new(
            Name: "x",
            Kind: kind,
            Path: "x",
            Detail: null,
            Expression: null,
            Description: null,
            Hidden: false,
            SourceColumn: null,
            Children: []);
}
