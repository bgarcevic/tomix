using Tomix.App.Rm;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

/// <summary>
/// <c>rm --from</c> input: plain object paths and JSON Lines in either shape, with unknown fields
/// ignored so other tools' scan output pipes straight in.
/// </summary>
public sealed class RemoveTargetListTests
{
    [Theory]
    [InlineData("Sales/Old", "Sales/Old", null)]
    [InlineData("""{"type":"measure","table":"Sales","name":"Old"}""", "Sales/Old", ModelObjectKind.Measure)]
    [InlineData("""{"type":"Calculated Column","table":"Sales","name":"Calc"}""", "Sales/Calc", ModelObjectKind.Column)]
    [InlineData("""{"type":"table","name":"Staging","reason":"unused","size":12}""", "Staging", ModelObjectKind.Table)]
    [InlineData("""{"type":"CalculatedTable","name":"Dates"}""", "Dates", ModelObjectKind.Table)]
    [InlineData("""{"type":"hierarchy","table":"Date","name":"Calendar"}""", "Date/Calendar", ModelObjectKind.Hierarchy)]
    [InlineData("""{"type":"level","table":"Date","hierarchy":"Calendar","name":"Year"}""", "Date/Calendar/Year", ModelObjectKind.Level)]
    [InlineData("""{"type":"kpi","table":"Sales","name":"Total"}""", "Sales/Total/KPI", ModelObjectKind.Kpi)]
    [InlineData("""{"type":"relationship","name":"Sales_Customer"}""", "Relationships/Sales_Customer", ModelObjectKind.Relationship)]
    [InlineData("""{"type":"role","name":"Readers"}""", "Roles/Readers", ModelObjectKind.Role)]
    [InlineData("""{"type":"member","role":"Readers","name":"a@b.com"}""", "Roles/Readers/a@b.com", ModelObjectKind.RoleMember)]
    [InlineData("""{"type":"tablepermission","role":"Readers","table":"Sales"}""", "Roles/Readers/Sales", ModelObjectKind.TablePermission)]
    [InlineData("""{"type":"perspective","name":"Sales Only"}""", "Perspectives/Sales Only", ModelObjectKind.Perspective)]
    [InlineData("""{"type":"culture","name":"da-DK"}""", "Cultures/da-DK", ModelObjectKind.Culture)]
    [InlineData("""{"type":"expression","name":"RangeStart"}""", "Expressions/RangeStart", ModelObjectKind.Expression)]
    [InlineData("""{"type":"function","name":"AddOne"}""", "Functions/AddOne", ModelObjectKind.Function)]
    [InlineData("""{"type":"datasource","name":"Warehouse"}""", "DataSources/Warehouse", ModelObjectKind.DataSource)]
    [InlineData("""{"type":"calendar","table":"Date","name":"Fiscal"}""", "Date/Fiscal", ModelObjectKind.Calendar)]
    [InlineData("""{"type":"measure","table":"A/B","name":"M"}""", "'A/B'/M", ModelObjectKind.Measure)]
    [InlineData("""{"Type":"Measure","Table":"Sales","Name":"Old"}""", "Sales/Old", ModelObjectKind.Measure)]
    [InlineData("""{"path":"Sales[Key] -> Customer[Key]"}""", "Sales[Key] -> Customer[Key]", null)]
    [InlineData("""{"type":"partition","path":"Sales/Sales"}""", "Sales/Sales", ModelObjectKind.Partition)]
    public void Parses_a_line(string line, string path, ModelObjectKind? type)
    {
        var parsed = RemoveTargetList.Parse(line);

        Assert.Empty(parsed.Errors);
        var target = Assert.Single(parsed.Targets);
        Assert.Equal(path, target.Path);
        Assert.Equal(type, target.Type);
        Assert.Equal(line, target.Label);
    }

    [Fact]
    public void Skips_blank_lines_comments_and_a_byte_order_mark()
    {
        var parsed = RemoveTargetList.Parse("﻿Sales/A\r\n\r\n# unused objects\r\n  Sales/B  \r\n");

        Assert.Empty(parsed.Errors);
        Assert.Equal(["Sales/A", "Sales/B"], parsed.Targets.Select(t => t.Path));
    }

    [Theory]
    [InlineData("""{"type":"measure","name":"Old"}""", "missing \"table\"")]
    [InlineData("""{"type":"gizmo","name":"Old"}""", "unknown type 'gizmo'")]
    [InlineData("""{"name":"Old"}""", "expected \"path\"")]
    [InlineData("""{"type":"level","table":"Date","name":"Year"}""", "\"hierarchy\"")]
    [InlineData("""{"type":"member","name":"a@b.com"}""", "missing \"role\"")]
    [InlineData("""{"type":"measure",""", "invalid JSON")]
    public void Reports_bad_lines_with_their_line_number(string line, string message)
    {
        var parsed = RemoveTargetList.Parse("Sales/Fine\n" + line);

        var error = Assert.Single(parsed.Errors);
        Assert.StartsWith("line 2: ", error);
        Assert.Contains(message, error);
    }
}
