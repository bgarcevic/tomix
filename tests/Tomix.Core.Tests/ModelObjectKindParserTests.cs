using Tomix.Core.Models;

namespace Tomix.Core.Tests;

public sealed class ModelObjectKindParserTests
{
    [Theory]
    [InlineData("table", ModelObjectKind.Table)]
    [InlineData("measure", ModelObjectKind.Measure)]
    [InlineData("column", ModelObjectKind.Column)]
    [InlineData("calculatedcolumn", ModelObjectKind.Column)]
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
    [InlineData("expression")]
    [InlineData("")]
    public void TryParse_UnknownStrings_ReturnFalse(string value)
    {
        Assert.False(ModelObjectKindParser.TryParse(value, out _));
    }
}
