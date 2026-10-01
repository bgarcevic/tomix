using Tomix.Core.Models;
using static Tomix.Provider.Tom.Tests.TestModels;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Runs every mutation kind on <see cref="JournalFixture.Rich"/> through both oracles of
/// <see cref="JournalOracle"/>: the journal's changes must agree with the snapshot diff, and
/// rolling the mutation back must restore the model, by swap and by <c>CopyTo</c>. A new
/// mutation path belongs in <see cref="Cases"/>.
/// </summary>
public sealed class TomChangeJournalOracleTests
{
    private const string PolicySource =
        "let Source = Sql.Database(\"srv\", \"db\"), Filtered = Table.SelectRows(Source, each [Date] >= RangeStart and [Date] < RangeEnd) in Filtered";

    private static readonly Dictionary<string, Action<TomModelMutator>> Mutations = new()
    {
        // set: one per object kind, plus renames that move descendants and relabel references
        ["set measure expression"] = m => m.SetProperty(Set("Sales/Revenue", "expression", "SUM(Sales[Amount]) * 2")),
        ["set measure several properties"] = m => m.SetProperty(new ModelObjectSetRequest("Sales/Revenue",
            [new("displayFolder", "Finance"), new("formatString", "0.0"), new("isHidden", "true")], null)),
        ["rename measure"] = m => m.SetProperty(Set("Sales/Revenue", "name", "Turnover")),
        ["rename table"] = m => m.SetProperty(Set("Sales", "name", "Orders")),
        ["set table description"] = m => m.SetProperty(Set("Sales", "description", "Order lines")),
        ["rename column in relationship"] = m => m.SetProperty(Set("Sales/CustomerId", "name", "CustomerKey")),
        ["set column sort by"] = m => m.SetProperty(Set("Sales/MonthName", "sortByColumn", "")),
        ["set column data type"] = m => m.SetProperty(Set("Sales/Amount", "dataType", "Decimal")),
        ["set calculated column expression"] = m => m.SetProperty(Set("Sales/Double", "expression", "[Amount] * 3")),
        ["set calc group precedence"] = m => m.SetProperty(Set("Time Intelligence", "precedence", "5")),
        ["set calc item expression"] = m => m.SetProperty(Set("Time Intelligence/YTD", "expression", "SELECTEDMEASURE() * 3")),
        ["rename calc item"] = m => m.SetProperty(Set("Time Intelligence/YTD", "name", "Year to date")),
        ["set partition expression"] = m => m.SetProperty(Set("Sales/Sales 2023", "expression", "let x = 2 in x")),
        ["set partition mode"] = m => m.SetProperty(Set("Sales/Sales 2023", "mode", "DirectQuery")),
        ["rename hierarchy"] = m => m.SetProperty(Set("Sales/Calendar", "name", "Months")),
        ["rename level"] = m => m.SetProperty(Set("Sales/Calendar/MonthNo", "name", "Month number")),
        ["set level description"] = m => m.SetProperty(Set("Sales/Calendar/MonthNo", "description", "Month number")),
        ["set kpi target"] = m => m.SetProperty(Set("Sales/Revenue/KPI", "targetExpression", "200")),
        ["rename role"] = m => m.SetProperty(Set("Readers", "name", "Viewers")),
        ["set role permission"] = m => m.SetProperty(Set("Readers", "modelPermission", "ReadRefresh")),
        ["set table permission filter"] = m => m.SetProperty(Set("Readers/Customer", "filterExpression", "[Id] > 10")),
        ["rename role member"] = m => m.SetProperty(Set("Readers/user@contoso.com", "name", "other@contoso.com")),
        ["set relationship activity"] = m => m.SetProperty(new ModelObjectSetRequest("Sales[CustomerId]->Customer[Id]",
            [new("isActive", "false"), new("crossFilteringBehavior", "BothDirections")], ModelObjectKind.Relationship)),
        ["rename expression"] = m => m.SetProperty(Set("Environment", "name", "Stage")),
        ["set function expression"] = m => m.SetProperty(Set("AddOne", "expression", "(x) => x + 2")),
        ["set data source"] = m => m.SetProperty(Set("Warehouse", "maxConnections", "5")),
        ["rename perspective"] = m => m.SetProperty(Set("Reporting", "name", "Board")),
        ["rename culture"] = m => m.SetProperty(Set("da-DK", "name", "nb-NO")),
        ["rename calendar"] = m => m.SetProperty(Set("Sales/Fiscal", "name", "Retail")),
        ["set model root"] = m => m.SetProperty(new ModelObjectSetRequest(".",
            [new("description", "Sales model"), new("discourageImplicitMeasures", "true")], null)),
        ["set annotation"] = m => m.SetProperty(Set("Sales/Revenue", "Annotation:Owner", "sales")),
        ["add annotation"] = m => m.SetProperty(Set("Sales/Revenue", "Annotation:Reviewed", "yes")),
        ["remove annotation"] = m => m.SetProperty(Set("Sales/Revenue", "Annotation:Owner", "")),
        ["set model annotation"] = m => m.SetProperty(Set(".", "Annotation:Team", "Data")),
        ["set translation"] = m => m.SetProperty(Set("Sales/Revenue", "translation:da-DK/caption", "Salg i alt")),
        ["add translation"] = m => m.SetProperty(Set("Sales/Count", "translation:da-DK/caption", "Antal")),
        ["remove translation"] = m => m.SetProperty(Set("Sales/Amount", "translation:da-DK/caption", "")),
        ["create refresh policy"] = m => m.SetProperty(new ModelObjectSetRequest("Sales/RefreshPolicy",
        [
            new("RollingWindowGranularity", "year"), new("RollingWindowPeriods", "10"),
            new("IncrementalGranularity", "day"), new("IncrementalPeriods", "3"), new("SourceExpression", PolicySource)
        ], null)),

        // add: every creatable type
        ["add table"] = m => m.AddObject(Add("Products", "Table")),
        ["add calc table"] = m => m.AddObject(Add("Dates", "CalcTable", "CALENDARAUTO()")),
        ["add calc group"] = m => m.AddObject(Add("Scenarios", "CalcGroup")),
        ["add measure"] = m => m.AddObject(Add("Sales/Profit", "Measure", "1")),
        ["add calculated column"] = m => m.AddObject(Add("Sales/Triple", "CalculatedColumn", "[Amount] * 3")),
        ["add data column"] = m => m.AddObject(Add("Sales/Region", "DataColumn")),
        ["add hierarchy"] = m => m.AddObject(Add("Sales/Amounts", "Hierarchy", "Amount")),
        ["add level"] = m => m.AddObject(Add("Sales/Calendar/Amount", "Level", "Amount")),
        ["add calendar"] = m => m.AddObject(Add("Customer/Retail", "Calendar")),
        ["add calc item"] = m => m.AddObject(Add("Time Intelligence/MTD", "CalcItem", "SELECTEDMEASURE()")),
        ["add kpi"] = m => m.AddObject(Add("Sales/Count", "KPI", "10")),
        ["add partition"] = m => m.AddObject(Add("Sales/Sales 2024", "MPartition")),
        ["add expression"] = m => m.AddObject(Add("Region", "Expression", "\"EU\"")),
        ["add function"] = m => m.AddObject(Add("Twice", "Function", "(x) => x * 2")),
        ["add perspective"] = m => m.AddObject(Add("Finance", "Perspective")),
        ["add culture"] = m => m.AddObject(Add("fr-FR", "Culture")),
        ["add provider data source"] = m => m.AddObject(Add("Lake", "ProviderDataSource")),
        ["add structured data source"] = m => m.AddObject(Add("Lakehouse", "StructuredDataSource")),
        ["add role"] = m => m.AddObject(Add("Admins", "Role")),
        ["add table permission"] = m => m.AddObject(Add("Readers/Sales", "TablePermission", "[Amount] > 0")),
        ["add role member"] = m => m.AddObject(Add("roles/Readers/members/new@contoso.com", null)),
        ["add relationship"] = m => m.AddObject(Add("Sales[MonthNo]->Customer[Name]", "Relationship")),

        // remove: every removable kind, with the cascades the fixture wires up
        ["remove table with cascades"] = m => m.RemoveObject(Remove("Customer")),
        ["remove column with cascades"] = m => m.RemoveObject(Remove("Sales/MonthNo")),
        ["remove column in relationship"] = m => m.RemoveObject(Remove("Sales/CustomerId")),
        ["remove measure"] = m => m.RemoveObject(Remove("Sales/Revenue")),
        ["remove hierarchy"] = m => m.RemoveObject(Remove("Sales/Calendar")),
        ["remove level"] = m => m.RemoveObject(Remove("Sales/Calendar/MonthNo")),
        ["remove partition"] = m => m.RemoveObject(Remove("Sales/Sales 2023")),
        ["remove role"] = m => m.RemoveObject(Remove("Readers")),
        ["remove role member"] = m => m.RemoveObject(Remove("Readers/user@contoso.com")),
        ["remove table permission"] = m => m.RemoveObject(Remove("Readers/Customer")),
        ["remove relationship"] = m => m.RemoveObject(Remove("Sales[CustomerId] -> Customer[Id]")),
        ["remove calc item"] = m => m.RemoveObject(Remove("Time Intelligence/YTD")),
        ["remove kpi"] = m => m.RemoveObject(Remove("Sales/Revenue/KPI")),
        ["remove calendar"] = m => m.RemoveObject(Remove("Sales/Fiscal")),
        ["remove perspective"] = m => m.RemoveObject(Remove("Reporting")),
        ["remove culture"] = m => m.RemoveObject(Remove("da-DK")),
        ["remove expression"] = m => m.RemoveObject(Remove("Environment")),
        ["remove function"] = m => m.RemoveObject(Remove("AddOne")),
        ["remove data source"] = m => m.RemoveObject(Remove("Warehouse")),
        ["remove refresh policy"] = m =>
        {
            m.SetProperty(new ModelObjectSetRequest("Sales/RefreshPolicy",
            [
                new("RollingWindowGranularity", "year"), new("RollingWindowPeriods", "10"),
                new("IncrementalGranularity", "day"), new("IncrementalPeriods", "3"), new("SourceExpression", PolicySource)
            ], null));
            m.RemoveObject(Remove("Sales/RefreshPolicy"));
        },

        // move, replace, rewrite
        ["move measure"] = m => m.MoveObject(Move("Sales/Revenue", "Metrics")),
        ["move and rename measure"] = m => m.MoveObject(Move("Sales/Revenue", "Metrics", "Total Revenue")),
        ["replace names"] = m => m.ReplaceText(Replace("Month", "Period", "names")),
        ["replace expressions"] = m => m.ReplaceText(Replace("SELECTEDMEASURE()", "SELECTEDMEASURE() + 0", "expressions")),
        ["rewrite expressions"] = m => m.RewriteExpressions(
        [
            new ModelExpressionEdit("Sales/Revenue", ModelObjectKind.Measure, "Expression", "SUM(Sales[Amount]) + 1"),
            new ModelExpressionEdit("Sales/Double", ModelObjectKind.CalculatedColumn, "Expression", "[Amount] * 4")
        ]),
    };

    public static TheoryData<string> Cases => new(Mutations.Keys);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Changes_MatchTheSnapshotDiff(string mutation)
    {
        var changes = JournalOracle.AssertEventsMatchDiff(JournalFixture.Rich(), Mutations[mutation]);

        Assert.NotEmpty(changes);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Rollback_BySwap_RestoresTheModelByteForByte(string mutation)
        => JournalOracle.AssertRollbackRestores(JournalFixture.Rich(), Mutations[mutation], TomCheckpointRestore.Swap);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Rollback_ByCopyTo_RestoresTheModelContent(string mutation)
        => JournalOracle.AssertRollbackRestores(JournalFixture.Rich(), Mutations[mutation], TomCheckpointRestore.CopyTo);
}
