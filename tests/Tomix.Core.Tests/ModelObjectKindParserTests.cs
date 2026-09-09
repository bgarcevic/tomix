using Tomix.Core.Models;

namespace Tomix.Core.Tests;

public sealed class ModelObjectKindParserTests
{
    [Theory]
    [InlineData("table", ModelObjectKind.Table)]
    [InlineData("measure", ModelObjectKind.Measure)]
    [InlineData("column", ModelObjectKind.Column)]
    [InlineData("calculatedcolumn", ModelObjectKind.CalculatedColumn)]
    [InlineData("hierarchy", ModelObjectKind.Hierarchy)]
    [InlineData("level", ModelObjectKind.Level)]
    [InlineData("partition", ModelObjectKind.Partition)]
    [InlineData("calculationitem", ModelObjectKind.CalculationItem)]
    [InlineData("calcitem", ModelObjectKind.CalculationItem)]
    [InlineData("member", ModelObjectKind.RoleMember)]
    [InlineData("rolemember", ModelObjectKind.RoleMember)]
    [InlineData("datasource", ModelObjectKind.DataSource)]
    [InlineData("relationship", ModelObjectKind.Relationship)]
    [InlineData("role", ModelObjectKind.Role)]
    [InlineData("perspective", ModelObjectKind.Perspective)]
    [InlineData("culture", ModelObjectKind.Culture)]
    [InlineData("kpi", ModelObjectKind.Kpi)]
    [InlineData("tablepermission", ModelObjectKind.TablePermission)]
    [InlineData("calendar", ModelObjectKind.Calendar)]
    [InlineData("expression", ModelObjectKind.Expression)]
    [InlineData("function", ModelObjectKind.Function)]
    public void TryParse_KnownStrings(string value, ModelObjectKind expected)
    {
        Assert.True(ModelObjectKindParser.TryParse(value, out var kind));
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData("KPI")]
    [InlineData("  Calendar  ")]
    [InlineData("TablePermission")]
    public void TryParse_IsCaseInsensitiveAndTrims(string value)
    {
        Assert.True(ModelObjectKindParser.TryParse(value, out _));
    }

    [Theory]
    [InlineData("kpis")]
    [InlineData("expressions")]
    [InlineData("functions")]
    [InlineData("")]
    public void TryParse_UnknownStrings_ReturnFalse(string value)
    {
        Assert.False(ModelObjectKindParser.TryParse(value, out _));
    }

    [Fact]
    public void DiscoveryCatalog_CoversEveryParsedKind_AndMatchesParser()
    {
        // Every advertised discovery token parses, and every kind with a token is reachable —
        // the parser and the help/error texts derive from the same list.
        var discovered = new HashSet<ModelObjectKind>();
        foreach (var entry in ModelObjectTypeCatalog.Discovery)
        {
            Assert.True(ModelObjectKindParser.TryParse(entry.Token, out var kind));
            Assert.Equal(entry.Kind, kind);
            foreach (var alias in entry.Aliases)
                Assert.True(ModelObjectKindParser.TryParse(alias, out var aliasKind) && aliasKind == entry.Kind);
            Assert.True(discovered.Add(entry.Kind));
        }

        Assert.Equal(ModelObjectTypeCatalog.Discovery.Count, discovered.Count);
        Assert.DoesNotContain(ModelObjectKind.Model, discovered);
    }

    [Theory]
    [InlineData(ModelObjectKind.Column, ModelObjectKind.Column, true)]
    [InlineData(ModelObjectKind.CalculatedColumn, ModelObjectKind.Column, true)]
    [InlineData(ModelObjectKind.Column, ModelObjectKind.CalculatedColumn, false)]
    [InlineData(ModelObjectKind.CalculatedColumn, ModelObjectKind.CalculatedColumn, true)]
    [InlineData(ModelObjectKind.Measure, ModelObjectKind.Column, false)]
    public void Matches_ColumnFilterIncludesCalculatedColumns(
        ModelObjectKind actual, ModelObjectKind requested, bool expected)
        => Assert.Equal(expected, actual.Matches(requested));
}
