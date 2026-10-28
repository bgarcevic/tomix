using Tomix.App.Validate;
using Tomix.Core.Models;
using Tomix.Core.Rules;
using Tomix.Provider.Tmdl;

namespace Tomix.App.Tests;

public sealed class ValidateModelHandlerTests
{
    [Fact]
    public async Task HandleAsync_ReturnsValid_WhenNoErrors()
    {
        var handler = new ValidateModelHandler([new StubModelProvider(ValidSnapshot())]);
        var result = await handler.HandleAsync(
            new ValidateModelRequest(new ModelReference("any"), ErrorsOnly: false, NoWarnings: false, ServerOnly: false),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Data!.Valid);
        Assert.Empty(result.Data.Errors);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_DetectsBrokenColumnReference()
    {
        var snapshot = SnapshotWithBrokenMeasure();
        var handler = new ValidateModelHandler([new StubModelProvider(snapshot)]);
        var result = await handler.HandleAsync(
            new ValidateModelRequest(new ModelReference("any"), ErrorsOnly: false, NoWarnings: false, ServerOnly: false),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Data!.Valid);
        Assert.NotEmpty(result.Data.Errors);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(result.Data.Errors, e => e.Code == "DAX0002" && e.Message.Contains("Missing"));
    }

    [Theory]
    [InlineData(ModelObjectKind.Measure, "[Self]")]
    [InlineData(ModelObjectKind.CalculatedColumn, "Sales[Self]")]
    public async Task HandleAsync_DetectsDirectDaxSelfReference(ModelObjectKind kind, string expression)
    {
        var obj = Measure("Self", expression) with { Kind = kind };
        var result = await ValidateAsync(SalesSnapshot(obj));

        Assert.False(result.Data!.Valid);
        var issue = Assert.Single(result.Data.Errors);
        Assert.Equal("DAX0006", issue.Code);
        Assert.Equal("Sales/Self", issue.ObjectName);
    }

    [Fact]
    public async Task HandleAsync_SkipsLocalAnalysis_WhenServerOnly()
    {
        var snapshot = SnapshotWithBrokenMeasure();
        var handler = new ValidateModelHandler([new StubModelProvider(snapshot)]);
        var result = await handler.HandleAsync(
            new ValidateModelRequest(new ModelReference("any"), ErrorsOnly: false, NoWarnings: false, ServerOnly: true),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Data!.Valid);
        Assert.Empty(result.Data.Errors);
    }

    [Fact]
    public async Task HandleAsync_ReturnsFail_WhenNoProviderMatches()
    {
        var handler = new ValidateModelHandler([]);
        var result = await handler.HandleAsync(
            new ValidateModelRequest(new ModelReference("any"), ErrorsOnly: false, NoWarnings: false, ServerOnly: false),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("TOMIX_NO_PROVIDER", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task HandleAsync_DeduplicatesIdenticalErrors()
    {
        var expression = "SUM(Sales[Missing]) + SUM(Sales[Missing])";
        var measure = new ModelObject("Broken", ModelObjectKind.Measure, "Sales/Broken",
            Detail: null, Expression: expression, Description: null, Hidden: false,
            SourceColumn: null, Children: []);
        var amount = new ModelObject("Amount", ModelObjectKind.Column, "Sales/Amount",
            Detail: "int64", Expression: null, Description: null, Hidden: false,
            SourceColumn: "Amount", Children: []);
        var sales = new ModelObject("Sales", ModelObjectKind.Table, "Sales",
            Detail: "regular", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: [amount, measure]);

        var snapshot = new ModelSnapshot("test", 1601, [sales]);
        var handler = new ValidateModelHandler([new StubModelProvider(snapshot)]);
        var result = await handler.HandleAsync(
            new ValidateModelRequest(new ModelReference("any"), ErrorsOnly: false, NoWarnings: false, ServerOnly: false),
            CancellationToken.None);

        Assert.Single(result.Data!.Errors);
    }

    [Fact]
    public async Task HandleAsync_SkipsMeasureReferences()
    {
        var snapshot = SnapshotWithMeasureReference();
        var handler = new ValidateModelHandler([new StubModelProvider(snapshot)]);
        var result = await handler.HandleAsync(
            new ValidateModelRequest(new ModelReference("any"), ErrorsOnly: false, NoWarnings: false, ServerOnly: false),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Data!.Valid);
        Assert.Empty(result.Data.Errors);
    }

    [Fact]
    public async Task HandleAsync_IgnoresReferencesInStringsAndComments()
    {
        var expression = "\"Sales[Nope]\" & \"x\" // Sales[AlsoNope]\n& FORMAT(SUM(Sales[Amount]), \"0\")";
        var result = await ValidateAsync(SalesSnapshot(Measure("Total", expression)));

        Assert.True(result.Data!.Valid);
        Assert.Empty(result.Data.Errors);
        Assert.Empty(result.Data.Warnings);
    }

    [Fact]
    public async Task HandleAsync_AcceptsQuotedCalendarReference()
    {
        // TOTALYTD([X], 'Fiscal') names a calendar, written like a table.
        var calendar = new ModelObject("Fiscal", ModelObjectKind.Calendar, "Sales/Fiscal",
            Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null, Children: []);
        var result = await ValidateAsync(SalesSnapshot(
            calendar, Measure("YTD", "TOTALYTD(SUM(Sales[Amount]), 'Fiscal')")));

        Assert.DoesNotContain(result.Data!.Errors, e => e.Code == "DAX0001");
    }

    [Fact]
    public async Task HandleAsync_DetectsUnknownTable()
    {
        var result = await ValidateAsync(SalesSnapshot(Measure("Total", "SUM('Missing Table'[X])")));

        Assert.False(result.Data!.Valid);
        Assert.Contains(result.Data.Errors, e => e.Code == "DAX0001" && e.Message.Contains("Missing Table"));
    }

    [Fact]
    public async Task HandleAsync_ReportsLineOfBrokenReference()
    {
        var result = await ValidateAsync(SalesSnapshot(Measure("Total", "SUM(Sales[Amount])\n+ SUM(Sales[Missing])")));

        var issue = Assert.Single(result.Data!.Errors);
        Assert.Equal("2", issue.Expression);
        Assert.Equal("+ SUM(Sales[Missing])", issue.ExpressionLine);
    }

    [Fact]
    public async Task HandleAsync_TruncatesVeryLongOffendingLine()
    {
        var name = new string('x', 200);
        var result = await ValidateAsync(SalesSnapshot(Measure("Total", $"SUM(Sales[{name}])")));

        var issue = Assert.Single(result.Data!.Errors);
        Assert.NotNull(issue.ExpressionLine);
        Assert.EndsWith("...", issue.ExpressionLine);
        Assert.Equal(120, issue.ExpressionLine!.Length);
    }

    [Fact]
    public async Task HandleAsync_CarriesMeasureNames_ForHighlighting()
    {
        var result = await ValidateAsync(SnapshotWithMeasureReference());

        Assert.True(result.Data!.Valid);
        Assert.NotNull(result.Data.MeasureNames);
        Assert.Contains("Total Revenue", result.Data.MeasureNames!);
    }

    [Fact]
    public async Task HandleAsync_LoadFailure_LeavesMeasureNamesNull()
    {
        var handler = new ValidateModelHandler([new ThrowingModelProvider()]);
        var result = await handler.HandleAsync(
            new ValidateModelRequest(new ModelReference("any"), ErrorsOnly: false, NoWarnings: false, ServerOnly: false),
            CancellationToken.None);

        Assert.Null(result.Data!.MeasureNames);
    }

    [Fact]
    public async Task HandleAsync_ReportsUnclosedGroup_AsSyntaxError()
    {
        var result = await ValidateAsync(SalesSnapshot(Measure("Total", "SUM(Sales[Amount]")));

        Assert.False(result.Data!.Valid);
        var issue = Assert.Single(result.Data.Errors);
        Assert.Equal("DAX0004", issue.Code);
        Assert.Contains("'(' has no matching", issue.Message);
        Assert.Equal("1", issue.Expression);
    }

    [Fact]
    public async Task HandleAsync_SyntaxError_SuppressesReferenceChecks()
    {
        // Sales[Missing] would be a DAX0002 if the reference scan ran; the unclosed group makes
        // everything after it read wrong, so only the syntax error is reported.
        var result = await ValidateAsync(SalesSnapshot(Measure("Total", "SUM(Sales[Missing]")));

        var issue = Assert.Single(result.Data!.Errors);
        Assert.Equal("DAX0004", issue.Code);
    }

    [Fact]
    public async Task HandleAsync_ReportsUnterminatedString_AsSyntaxError()
    {
        var result = await ValidateAsync(SalesSnapshot(Measure("Total", "\"Sales[Nope] & SUM(Sales[Amount])")));

        var issue = Assert.Single(result.Data!.Errors);
        Assert.Equal("DAX0005", issue.Code);
        Assert.Contains("Unterminated string literal", issue.Message);
    }

    [Theory]
    [InlineData("SUM(Sales[Amount] Sales[Amount])", "DAX0007", "Expected ',' or ')'")]
    [InlineData("SUM(Sales[Amount]) +", "DAX0008", "Expected an expression")]
    [InlineData("VAR x = SUM(Sales[Amount])", "DAX0009", "VAR block has no RETURN")]
    public async Task HandleAsync_ReportsGrammarError_WithItsOwnCode(string expression, string code, string message)
    {
        // Balanced brackets and resolvable references: only the parser can see these are broken.
        var result = await ValidateAsync(SalesSnapshot(Measure("Total", expression)));

        var issue = Assert.Single(result.Data!.Errors);
        Assert.Equal(code, issue.Code);
        Assert.Contains(message, issue.Message);
    }

    [Fact]
    public async Task HandleAsync_ReportsLineOfSyntaxError()
    {
        var result = await ValidateAsync(SalesSnapshot(Measure("Total", "SUM(Sales[Amount])\n+ ( SUM(Sales[Amount])")));

        var issue = Assert.Single(result.Data!.Errors);
        Assert.Equal("DAX0004", issue.Code);
        Assert.Equal("2", issue.Expression);
    }

    [Fact]
    public async Task HandleAsync_WarnsOnUnresolvedUnqualifiedReference()
    {
        var result = await ValidateAsync(SalesSnapshot(Measure("Total", "[No Such Measure] + 1")));

        Assert.True(result.Data!.Valid);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.Data.Warnings, w => w.Code == "DAX0003" && w.Message.Contains("No Such Measure"));
    }

    // A lone column must be on a table DAX can bind it to: one iterated around it, or the
    // expression's own table. Region is only on Geo; Sales has none. Outside every iterator DAX
    // rejects it (measured live), so it is an error.
    [Theory]
    [InlineData("DISTINCTCOUNT([Region])")]
    [InlineData("CALCULATE(COUNTROWS(Geo), [Region] = \"EU\")")]
    [InlineData("LEN([Region])")]
    public async Task HandleAsync_FailsOnALoneColumnOutsideEveryIteratorItsTableLacks(string expression)
    {
        var result = await ValidateAsync(SalesAndGeoSnapshot(Measure("Total", expression)));

        Assert.False(result.Data!.Valid);
        var error = Assert.Single(result.Data.Errors);
        Assert.Equal("DAX0010", error.Code);
        Assert.Equal(RuleSeverity.Error, error.Severity);
        Assert.Contains("Column [Region] is not on table 'Sales'", error.Message);
        Assert.Contains("'Geo'[Region]", error.Message);
    }

    [Fact]
    public async Task HandleAsync_FailsOnACalculatedColumnReadingAnotherTablesColumnLoneHanded()
    {
        var column = new ModelObject("Copy", ModelObjectKind.CalculatedColumn, "Sales/Copy",
            Detail: "string", Expression: "[Region]", Description: null, Hidden: false, SourceColumn: null, Children: []);

        var result = await ValidateAsync(SalesAndGeoSnapshot(column));

        Assert.Equal("DAX0010", Assert.Single(result.Data!.Errors).Code);
    }

    // Inside an iterator a function the analysis has no rule for could still supply the row.
    [Theory]
    [InlineData("SUMX(Sales, LEN([Region]))")]
    [InlineData("SUMX(VALUES(Geo[Code]), LEN([Region]))")]
    public async Task HandleAsync_WarnsOnALoneColumnNoIteratedTableHas(string expression)
    {
        var result = await ValidateAsync(SalesAndGeoSnapshot(Measure("Total", expression)));

        Assert.True(result.Data!.Valid);
        var warning = Assert.Single(result.Data.Warnings);
        Assert.Equal("DAX0003", warning.Code);
        Assert.Contains("Column [Region] is not on table 'Sales'", warning.Message);
        Assert.Contains("'Geo'[Region]", warning.Message);
    }

    [Theory]
    [InlineData("SUMX(Geo, LEN([Region]))")]
    [InlineData("COUNTROWS(FILTER(Geo, [Region] = \"EU\"))")]
    [InlineData("VAR g = FILTER(Geo, TRUE()) RETURN COUNTROWS(FILTER(g, [Region] = \"EU\"))")]
    [InlineData("SUMX(SUMMARIZE(Geo, Geo[Region]), LEN([Region]))")]
    [InlineData("SUMX(Geo, SUMX(Sales, LEN(EARLIER([Region]))))")]
    [InlineData("SUMX(DATESYTD(Geo[Code]), LEN([Region]))")]
    // Window functions read ORDERBY/PARTITIONBY columns from their relation (measured live).
    [InlineData("COUNTROWS(OFFSET(-1, ALL(Geo[Region]), ORDERBY([Region])))")]
    [InlineData("RANK(DENSE, ALL(Geo[Code], Geo[Region]), ORDERBY([Code]), , PARTITIONBY([Region]))")]
    [InlineData("COUNTROWS(WINDOW(1, ABS, 3, ABS, ALL(Geo[Region]), ORDERBY([Region])))")]
    // No relation: the columns come from the filter context, which is not tracked.
    [InlineData("COUNTROWS(OFFSET(-1, ORDERBY([Region])))")]
    public async Task HandleAsync_AcceptsALoneColumnATableAroundItMayHave(string expression)
    {
        var result = await ValidateAsync(SalesAndGeoSnapshot(Measure("Total", expression)));

        Assert.Empty(result.Data!.Errors);
        Assert.Empty(result.Data.Warnings);
    }

    [Theory]
    [InlineData("Geo", null)]
    [InlineData("Sales", "DAX0010")]
    public async Task HandleAsync_JudgesARoleFiltersLoneColumnAgainstItsTable(string table, string? code)
    {
        var result = await ValidateAsync(SalesAndGeoSnapshot(Role("Reader", Permission("Reader", table, "[Region] = \"EU\""))));

        Assert.Equal(code, result.Data!.Errors.Concat(result.Data.Warnings).SingleOrDefault()?.Code);
    }

    [Fact]
    public async Task HandleAsync_CarriesSeverity_OnEveryIssue()
    {
        var result = await ValidateAsync(SalesSnapshot(
            Measure("Total", "SUM(Sales[Missing])"),
            Measure("Loose", "[No Such Measure] + 1")));

        // Every error issue declares Error severity; warnings declare Warning (and only
        // warnings keep the model "valid").
        Assert.All(result.Data!.Errors, e => Assert.Equal(RuleSeverity.Error, e.Severity));
        var warning = Assert.Single(result.Data.Warnings);
        Assert.Equal("DAX0003", warning.Code);
        Assert.Equal(RuleSeverity.Warning, warning.Severity);
    }

    [Fact]
    public async Task HandleAsync_SuppressesWarnings_WhenNoWarnings()
    {
        var result = await ValidateAsync(
            SalesSnapshot(Measure("Total", "[No Such Measure] + 1")),
            noWarnings: true);

        Assert.True(result.Data!.Valid);
        Assert.Empty(result.Data.Warnings);
    }

    [Fact]
    public async Task HandleAsync_SkipsVarNamesAndFunctions()
    {
        var expression = "VAR Segment = SUM(Sales[Amount]) RETURN Segment + COUNTROWS(Sales)";
        var result = await ValidateAsync(SalesSnapshot(Measure("Total", expression)));

        Assert.True(result.Data!.Valid);
        Assert.Empty(result.Data.Warnings);
    }

    [Fact]
    public async Task HandleAsync_SkipsMQueryPartitions()
    {
        var partition = new ModelObject("Part", ModelObjectKind.Partition, "Sales/Part",
            Detail: "m", Expression: "Table.SelectRows(Src, each Nope[Broken] > 0)", Description: null,
            Hidden: false, SourceColumn: null, Children: [],
            Properties: new Dictionary<string, string> { ["PartitionSourceType"] = "M" });
        var result = await ValidateAsync(SalesSnapshot(partition));

        Assert.True(result.Data!.Valid);
        Assert.Empty(result.Data.Errors);
        Assert.Empty(result.Data.Warnings);
    }

    [Theory]
    [InlineData("DATATABLE(\"Group\", STRING, \"Offset\", INTEGER, {{\"a, b\", 1}, {\"c\", BLANK()}})", "Axis[Group] + Axis[Offset]", null)]
    [InlineData("DATATABLE(\"Group\", STRING, {{\"a\"}})", "Axis[Missing]", "DAX0002")]
    [InlineData("-- axis\nROW(\"Group\", \"x\", \"Offset\", 1)", "Axis[Offset]", null)]
    [InlineData("ROW(\"Group\", \"x\")", "Axis[Missing]", "DAX0002")]
    // Columns that cannot be inferred offline are not reported either way.
    [InlineData("SELECTCOLUMNS(Sales, \"Group\", Sales[Amount])", "Axis[Anything]", null)]
    [InlineData("FILTER(DATATABLE(\"Group\", STRING, {{\"a\"}}), TRUE())", "Axis[Anything]", null)]
    public async Task HandleAsync_InfersUndeclaredCalculatedTableColumns(
        string tableExpression, string measureExpression, string? expectedCode)
    {
        var partition = new ModelObject("Axis", ModelObjectKind.Partition, "Axis/Axis",
            Detail: "calculated", Expression: tableExpression, Description: null,
            Hidden: false, SourceColumn: null, Children: []);
        var axis = new ModelObject("Axis", ModelObjectKind.Table, "Axis",
            Detail: "calculated", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: [partition]);
        var snapshot = SalesSnapshot(Measure("Total", measureExpression));
        var result = await ValidateAsync(snapshot with { Objects = [.. snapshot.Objects, axis] });

        if (expectedCode is null)
            Assert.Empty(result.Data!.Errors);
        else
            Assert.Equal(expectedCode, Assert.Single(result.Data!.Errors).Code);
    }

    [Theory]
    [InlineData("COUNTROWS(FILTER(ADDCOLUMNS(Sales, \"@Required\", 1, \"@Passed\", 2), [@Required] > 0 && [@Required] = [@Passed]))", null)]
    [InlineData("SUMX(GENERATESERIES(1, 12), [Value])", null)]
    [InlineData("SUMX({ (1, 2) }, [Value1] + [Value2])", null)]
    [InlineData("COUNTROWS(FILTER(ADDCOLUMNS(Sales, \"@Required\", 1), [@Requiredd] > 0))", "@Requiredd")]
    [InlineData("SUMX(Sales, [Value])", "Value")]
    // "@Required" in a comment defines nothing.
    [InlineData("SUMX(Sales, [@Required]) // \"@Required\"", "@Required")]
    [InlineData("SUMX(SELECTCOLUMNS(Sales, \"@A\", 1), [@A])", null)]
    [InlineData("SUMX(SUMMARIZE(Sales, Sales[Amount], \"@Total\", 1), [@Total])", null)]
    [InlineData("SUMX(SUMMARIZECOLUMNS(Sales[Amount], \"@Total\", 1), [@Total])", null)]
    [InlineData("SUMX(ROW(\"@One\", 1), [@One])", null)]
    // A string that is a value, not a column name, defines nothing.
    [InlineData("IF([Status] = \"Status\", 1)", "Status")]
    [InlineData("SUMX(ADDCOLUMNS(Sales, \"@Kind\", \"@Other\"), [@Other])", "@Other")]
    // IN { ... } is a list and DATATABLE rows are literals, not a [Value] table.
    [InlineData("IF(1 IN { 1, 2 }, [Value])", "Value")]
    [InlineData("SUMX(DATATABLE(\"N\", INTEGER, {{1}}), [Value])", "Value")]
    // Built by the expression, but used outside the table that has it.
    [InlineData("COUNTROWS(FILTER(ADDCOLUMNS(Sales, \"@Required\", 1), TRUE())) + [@Required]", "@Required")]
    [InlineData("VAR T = ADDCOLUMNS(Sales, \"@Required\", 1) RETURN SUMX(T, [@Required])", null)]
    // The server defines INFO.* columns, so a bare name there cannot be judged offline.
    [InlineData("COUNTROWS(FILTER(INFO.VIEW.MEASURES(), [IsHidden]))", null)]
    public async Task HandleAsync_AcceptsQueryScopedColumns(string expression, string? warnedName)
    {
        var result = await ValidateAsync(SalesSnapshot(Measure("Total", expression)));

        Assert.Empty(result.Data!.Errors);
        if (warnedName is null)
            Assert.Empty(result.Data.Warnings);
        else
            Assert.Contains($"[{warnedName}]", Assert.Single(result.Data.Warnings).Message);
    }

    [Fact]
    public async Task HandleAsync_ScansSecondaryMeasureExpressions()
    {
        var measure = new ModelObject("Total", ModelObjectKind.Measure, "Sales/Total",
            Detail: null, Expression: "SUM(Sales[Amount])", Description: null, Hidden: false,
            SourceColumn: null, Children: [],
            Properties: new Dictionary<string, string> { ["DetailRowsExpression"] = "SELECTCOLUMNS(Sales, \"A\", Sales[Missing])" });
        var result = await ValidateAsync(SalesSnapshot(measure));

        Assert.False(result.Data!.Valid);
        Assert.Contains(result.Data.Errors, e => e.Code == "DAX0002" && e.Message.Contains("Missing"));
    }

    [Theory]
    [InlineData("SUM(Sales[Missing])", "DAX0002")]
    [InlineData("SUM(Sales[Amount]", "DAX0004")]
    public async Task HandleAsync_ScansCalculatedColumnExpressions(string expression, string expectedCode)
    {
        var column = new ModelObject("Calc", ModelObjectKind.CalculatedColumn, "Sales/Calc",
            Detail: "decimal", Expression: expression, Description: null, Hidden: false,
            SourceColumn: null, Children: []);
        var result = await ValidateAsync(SalesSnapshot(column));

        Assert.False(result.Data!.Valid);
        Assert.Contains(result.Data.Errors, e => e.Code == expectedCode);
    }

    [Fact]
    public async Task HandleAsync_AdventureWorksSample_ReportsNoErrors()
    {
        // End-to-end guard for the enum-split regressions: the sample's Product/Sorting and
        // Date/Year calculated columns are referenced by a sort-by and by another expression,
        // so a clean run requires the name index and the DAX scanner to see them both.
        var handler = new ValidateModelHandler([new TmdlModelProvider()]);
        var result = await handler.HandleAsync(
            new ValidateModelRequest(
                new ModelReference(SampleModel.Locate("AdventureWorks Sales.SemanticModel")),
                ErrorsOnly: false, NoWarnings: false, ServerOnly: false),
            CancellationToken.None);

        Assert.True(result.Data!.Valid);
        Assert.Empty(result.Data.Errors);
    }

    [Fact]
    public async Task HandleAsync_BasicTmdlSample_ResolvesBannerNameFromFolder()
    {
        // Banner-name pin: the sample's database carries no TOM name and no .platform file, so
        // the display name resolves through the folder branch of ModelDisplayName — never
        // "(unnamed)".
        var handler = new ValidateModelHandler([new TmdlModelProvider()]);
        var result = await handler.HandleAsync(
            new ValidateModelRequest(
                new ModelReference(SampleModel.Locate()),
                ErrorsOnly: false, NoWarnings: false, ServerOnly: false),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("basic-tmdl", result.Data!.ModelName);
    }

    [Fact]
    public async Task HandleAsync_DetectsBrokenRelationshipEndpoint()
    {
        var relationship = new ModelObject("Sales[Key] -> Product[Key]", ModelObjectKind.Relationship,
            "Relationships/Sales[Key] -> Product[Key]", Detail: null, Expression: null, Description: null,
            Hidden: false, SourceColumn: null, Children: [],
            Properties: new Dictionary<string, string>
            {
                ["FromColumn"] = "Sales[Key]",
                ["ToColumn"] = "Product[Key]"
            });
        var result = await ValidateAsync(SalesSnapshot(relationship));

        Assert.False(result.Data!.Valid);
        Assert.Contains(result.Data.Errors, e => e.Code == "TOMIX_BROKEN_RELATIONSHIP");
    }

    [Fact]
    public async Task HandleAsync_AcceptsReferencesToCalculatedColumns()
    {
        // Sales/Year is a calculated column; a measure referencing it must not report DAX0002.
        var year = new ModelObject("Year", ModelObjectKind.CalculatedColumn, "Sales/Year",
            Detail: "int64", Expression: "Sales[Amount] * 1", Description: null, Hidden: false,
            SourceColumn: null, Children: []);
        var result = await ValidateAsync(SalesSnapshot(year, Measure("Latest", "MAX(Sales[Year])")));

        Assert.True(result.Data!.Valid);
        Assert.Empty(result.Data.Errors);
        Assert.Empty(result.Data.Warnings);
    }

    [Theory]
    [InlineData(ModelObjectKind.Column)]
    [InlineData(ModelObjectKind.CalculatedColumn)]
    public async Task HandleAsync_DetectsBrokenSortByColumn(ModelObjectKind kind)
    {
        var column = new ModelObject("Month", kind, "Sales/Month",
            Detail: "string", Expression: kind == ModelObjectKind.CalculatedColumn ? "1" : null,
            Description: null, Hidden: false,
            SourceColumn: kind == ModelObjectKind.Column ? "Month" : null, Children: [],
            Properties: new Dictionary<string, string> { ["SortByColumn"] = "MonthNo" });
        var result = await ValidateAsync(SalesSnapshot(column));

        Assert.False(result.Data!.Valid);
        Assert.Contains(result.Data.Errors, e => e.Code == "TOMIX_BROKEN_SORT_BY" && e.Message.Contains("MonthNo"));
    }

    [Fact]
    public async Task HandleAsync_DetectsBrokenHierarchyLevel()
    {
        var level = new ModelObject("Year", ModelObjectKind.Level, "Sales/Calendar/Year",
            Detail: "YearCol", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: []);
        var hierarchy = new ModelObject("Calendar", ModelObjectKind.Hierarchy, "Sales/Calendar",
            Detail: "1 levels", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: [level]);
        var result = await ValidateAsync(SalesSnapshot(hierarchy));

        Assert.False(result.Data!.Valid);
        Assert.Contains(result.Data.Errors, e => e.Code == "TOMIX_BROKEN_LEVEL" && e.Message.Contains("YearCol"));
    }

    [Fact]
    public async Task HandleAsync_ReportsLoadFailureAsValidationError()
    {
        var handler = new ValidateModelHandler([new ThrowingModelProvider()]);
        var result = await handler.HandleAsync(
            new ValidateModelRequest(new ModelReference("any"), ErrorsOnly: false, NoWarnings: false, ServerOnly: false),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Data!.Valid);
        Assert.Equal(1, result.ExitCode);
        var issue = Assert.Single(result.Data.Errors);
        Assert.Equal("TOMIX_MODEL_LOAD_FAILED", issue.Code);
        Assert.Contains("cannot be resolved", issue.Message);
        // No snapshot exists on a load failure, so the model reference stands in as the name.
        Assert.Equal("any", result.Data!.ModelName);
    }

    [Fact]
    public async Task HandleAsync_CarriesSnapshotName_AsModelName()
    {
        var result = await ValidateAsync(ValidSnapshot());

        Assert.True(result.Data!.Valid);
        Assert.Equal("test", result.Data.ModelName);
    }

    [Fact]
    public async Task HandleAsync_PreservesAuthRequiredDiagnostic()
    {
        var handler = new ValidateModelHandler(
            [new ThrowingModelProvider(new Tomix.Core.Authentication.AuthenticationRequiredException("Not authenticated. Run 'tx auth login'."))]);
        var result = await handler.HandleAsync(
            new ValidateModelRequest(new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/ws", "Model"),
                ErrorsOnly: false, NoWarnings: false, ServerOnly: true),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Equal("TOMIX_AUTH_REQUIRED", result.Diagnostics[0].Code);
        Assert.Contains("auth login", result.Diagnostics[0].Hint);
    }

    [Fact]
    public async Task HandleAsync_ReportsRemoteFailureAsConnectDiagnostic()
    {
        var handler = new ValidateModelHandler(
            [new ThrowingModelProvider(new InvalidOperationException("connection refused"))]);
        var result = await handler.HandleAsync(
            new ValidateModelRequest(new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/ws", "Model"),
                ErrorsOnly: false, NoWarnings: false, ServerOnly: true),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Equal("TOMIX_CONNECT_FAILED", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task HandleAsync_ChecksEachRlsFilterOnItsOwn()
    {
        // The role's summary joins its filters as "Table: <DAX>" lines, which is not DAX: a
        // second filter used to fail with "Unexpected ':'" whenever its first line had no '='.
        var result = await ValidateAsync(SalesSnapshot(Role("Reader",
            Permission("Reader", "Sales", "CONTAINS ( Sales, Sales[Amount], 1 )"),
            Permission("Reader", "Region", "NOT ISBLANK ( Sales[Amount] )"))));

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Errors);
    }

    [Fact]
    public async Task HandleAsync_ReportsBrokenRlsFilter_OnItsTablePermission()
    {
        var result = await ValidateAsync(SalesSnapshot(Role("Reader",
            Permission("Reader", "Sales", "TRUE ()"),
            Permission("Reader", "Region", "Sales[Amount] > 0\n&& Sales[Missing] > 0"))));

        var issue = Assert.Single(result.Data!.Errors);
        Assert.Equal("DAX0002", issue.Code);
        Assert.Equal("Roles/Reader/Region", issue.ObjectName);
        Assert.Equal("2", issue.Expression);
    }

    [Theory]
    [InlineData("NoSelectionExpression")]
    [InlineData("MultipleOrEmptySelectionExpression")]
    public async Task HandleAsync_ChecksCalculationGroupSelectionExpressions(string property)
    {
        var group = new ModelObject("Scenario", ModelObjectKind.Table, "Scenario",
            Detail: "calculationGroup", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: [],
            Properties: new Dictionary<string, string> { [property] = "SUM ( Sales[Missing] )" });
        var snapshot = SalesSnapshot();
        snapshot = snapshot with { Objects = [.. snapshot.Objects, group] };

        var issue = Assert.Single((await ValidateAsync(snapshot)).Data!.Errors);
        Assert.Equal("DAX0002", issue.Code);
        Assert.Equal("Scenario", issue.ObjectName);
    }

    [Fact]
    public async Task HandleAsync_ChecksCalculationItemFormatStringExpression()
    {
        var item = new ModelObject("Current", ModelObjectKind.CalculationItem, "Sales/Current",
            Detail: null, Expression: "SELECTEDMEASURE ()", Description: null, Hidden: false,
            SourceColumn: null, Children: [],
            Properties: new Dictionary<string, string> { ["FormatStringExpression"] = "IF ( Sales[Missing] > 0, \"0\" )" });

        var issue = Assert.Single((await ValidateAsync(SalesSnapshot(item))).Data!.Errors);
        Assert.Equal("DAX0002", issue.Code);
        Assert.Equal("Sales/Current", issue.ObjectName);
    }

    private sealed class ThrowingModelProvider : IModelProvider
    {
        private readonly Exception _exception;

        public ThrowingModelProvider(Exception? exception = null)
            => _exception = exception ?? new InvalidOperationException("Property FromColumn cannot be resolved.");

        public bool CanOpen(ModelReference _) => true;

        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken ct)
            => throw _exception;
    }

    private static Task<Tomix.Core.Results.TomixResult<ValidateModelResult>> ValidateAsync(
        ModelSnapshot snapshot, bool noWarnings = false)
        => new ValidateModelHandler([new StubModelProvider(snapshot)]).HandleAsync(
            new ValidateModelRequest(new ModelReference("any"), ErrorsOnly: false, NoWarnings: noWarnings, ServerOnly: false),
            CancellationToken.None);

    private static ModelObject Measure(string name, string expression)
        => new(name, ModelObjectKind.Measure, $"Sales/{name}",
            Detail: null, Expression: expression, Description: null, Hidden: false,
            SourceColumn: null, Children: []);

    /// <summary>
    /// A role shaped like the TOM snapshot: each filter on a table permission child, and the
    /// role's display-only RlsExpression joining them as "Table: filter" lines.
    /// </summary>
    private static ModelObject Role(string name, params ModelObject[] permissions)
        => new(name, ModelObjectKind.Role, $"Roles/{name}",
            Detail: "Read", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: permissions,
            Properties: new Dictionary<string, string>
            {
                ["RlsExpression"] = string.Join("\n", permissions.Select(p => $"{p.Name}: {p.Expression}"))
            });

    private static ModelObject Permission(string role, string table, string filter)
        => new(table, ModelObjectKind.TablePermission, $"Roles/{role}/{table}",
            Detail: "default", Expression: filter, Description: null, Hidden: false,
            SourceColumn: null, Children: []);

    /// <summary>A Sales table with an Amount column, plus <paramref name="extras"/> as its children
    /// (relationships and roles are hoisted to the snapshot root, as in real snapshots).</summary>
    private static ModelSnapshot SalesSnapshot(params ModelObject[] extras)
    {
        var amount = new ModelObject("Amount", ModelObjectKind.Column, "Sales/Amount",
            Detail: "decimal", Expression: null, Description: null, Hidden: false,
            SourceColumn: "Amount", Children: []);
        var children = new List<ModelObject> { amount };
        var roots = new List<ModelObject>();
        foreach (var extra in extras)
        {
            if (extra.Kind is ModelObjectKind.Relationship or ModelObjectKind.Role)
                roots.Add(extra);
            else
                children.Add(extra);
        }

        var sales = new ModelObject("Sales", ModelObjectKind.Table, "Sales",
            Detail: "regular", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: children);

        return new ModelSnapshot("test", 1601, [sales, .. roots]);
    }

    /// <summary><see cref="SalesSnapshot"/> plus a Geo table with Code and Region columns.</summary>
    private static ModelSnapshot SalesAndGeoSnapshot(params ModelObject[] extras)
    {
        var sales = SalesSnapshot(extras);
        ModelObject Column(string name) => new(name, ModelObjectKind.Column, $"Geo/{name}",
            Detail: "string", Expression: null, Description: null, Hidden: false, SourceColumn: name, Children: []);
        var geo = new ModelObject("Geo", ModelObjectKind.Table, "Geo",
            Detail: "regular", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: [Column("Code"), Column("Region")]);

        return sales with { Objects = [sales.Objects[0], geo, .. sales.Objects.Skip(1)] };
    }

    private static ModelSnapshot SnapshotWithMeasureReference()
    {
        var amount = new ModelObject("Amount", ModelObjectKind.Column, "Sales/Amount",
            Detail: "decimal", Expression: null, Description: null, Hidden: false,
            SourceColumn: "Amount", Children: []);
        var otherMeasure = new ModelObject("Total Revenue", ModelObjectKind.Measure, "Sales/Total Revenue",
            Detail: null, Expression: "SUM(Sales[Amount])", Description: null, Hidden: false,
            SourceColumn: null, Children: []);
        var measure = new ModelObject("Total Sales", ModelObjectKind.Measure, "Sales/Total Sales",
            Detail: null, Expression: "Sales[Total Revenue]", Description: null, Hidden: false,
            SourceColumn: null, Children: []);
        var sales = new ModelObject("Sales", ModelObjectKind.Table, "Sales",
            Detail: "regular", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: [amount, otherMeasure, measure]);

        return new ModelSnapshot("test", 1601, [sales]);
    }

    private static ModelSnapshot ValidSnapshot()
    {
        var amount = new ModelObject("Amount", ModelObjectKind.Column, "Sales/Amount",
            Detail: "decimal", Expression: null, Description: null, Hidden: false,
            SourceColumn: "Amount", Children: []);
        var measure = new ModelObject("Total Sales", ModelObjectKind.Measure, "Sales/Total Sales",
            Detail: null, Expression: "SUM(Sales[Amount])", Description: null, Hidden: false,
            SourceColumn: null, Children: []);
        var sales = new ModelObject("Sales", ModelObjectKind.Table, "Sales",
            Detail: "regular", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: [amount, measure]);

        return new ModelSnapshot("test", 1601, [sales]);
    }

    private static ModelSnapshot SnapshotWithBrokenMeasure()
    {
        var amount = new ModelObject("Amount", ModelObjectKind.Column, "Sales/Amount",
            Detail: "decimal", Expression: null, Description: null, Hidden: false,
            SourceColumn: "Amount", Children: []);
        var measure = new ModelObject("Total Sales", ModelObjectKind.Measure, "Sales/Total Sales",
            Detail: null, Expression: "SUM(Sales[Missing])", Description: null, Hidden: false,
            SourceColumn: null, Children: []);
        var sales = new ModelObject("Sales", ModelObjectKind.Table, "Sales",
            Detail: "regular", Expression: null, Description: null, Hidden: false,
            SourceColumn: null, Children: [amount, measure]);

        return new ModelSnapshot("test", 1601, [sales]);
    }

    private sealed class StubModelProvider : IModelProvider
    {
        private readonly ModelSnapshot _snapshot;

        public StubModelProvider(ModelSnapshot snapshot) => _snapshot = snapshot;

        public bool CanOpen(ModelReference _) => true;

        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken ct)
            => Task.FromResult<IModelSession>(new StubSession(_snapshot));
    }

    private sealed class StubSession : IModelSession
    {
        private readonly ModelSnapshot _snapshot;

        public StubSession(ModelSnapshot snapshot) => _snapshot = snapshot;

        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 1, 1, 1, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(_snapshot);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
