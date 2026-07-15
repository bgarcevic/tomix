using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Provider.Tom;
using CompatibilityMode = Microsoft.AnalysisServices.CompatibilityMode;
using TabularJsonSerializer = Microsoft.AnalysisServices.Tabular.JsonSerializer;

namespace Tomix.Provider.Tom.Tests;

public sealed class TomRefreshPolicyManagerTests
{
    private const string ValidSourceExpression =
        "let Source = Sql.Database(\"srv\", \"db\"), Filtered = Table.SelectRows(Source, each [Date] >= RangeStart and [Date] < RangeEnd) in Filtered";

    [Fact]
    public void Set_CreatesPolicy_WithRequiredOptions()
    {
        var db = BaseModel();
        var result = new TomRefreshPolicyManager(db).Set(CreateRequest());

        Assert.True(result.Created);
        var policy = Assert.IsType<BasicRefreshPolicy>(db.Model.Tables["Sales"].RefreshPolicy);
        Assert.Equal(RefreshGranularityType.Year, policy.RollingWindowGranularity);
        Assert.Equal(10, policy.RollingWindowPeriods);
        Assert.Equal(RefreshGranularityType.Day, policy.IncrementalGranularity);
        Assert.Equal(3, policy.IncrementalPeriods);
        Assert.Equal(RefreshPolicyMode.Import, policy.Mode);
        Assert.Equal(ValidSourceExpression, policy.SourceExpression);
    }

    [Fact]
    public void Set_AutoCreatesRangeParameters_AndReportsThem()
    {
        var db = BaseModel();
        var result = new TomRefreshPolicyManager(db).Set(CreateRequest());

        Assert.Equal(["RangeStart", "RangeEnd"], result.CreatedExpressions);
        foreach (var name in new[] { "RangeStart", "RangeEnd" })
        {
            var expression = db.Model.Expressions.Find(name);
            Assert.NotNull(expression);
            Assert.Equal(ExpressionKind.M, expression!.Kind);
            Assert.Contains("IsParameterQuery=true", expression.Expression);
            Assert.Contains("Type=\"DateTime\"", expression.Expression);
        }
    }

    [Fact]
    public void Set_DoesNotDuplicate_ExistingRangeParameters()
    {
        var db = BaseModel();
        AddRangeParameters(db);

        var result = new TomRefreshPolicyManager(db).Set(CreateRequest());

        Assert.Empty(result.CreatedExpressions);
        Assert.Equal(1, db.Model.Expressions.Count(e => e.Name == "RangeStart"));
    }

    [Fact]
    public void Set_Edit_OnlyTouchesProvidedFields()
    {
        var db = BaseModel();
        var manager = new TomRefreshPolicyManager(db);
        manager.Set(CreateRequest());

        var result = manager.Set(new RefreshPolicySetRequest(
            "Sales", Mode: null, RollingWindowGranularity: null, RollingWindowPeriods: null,
            IncrementalGranularity: null, IncrementalPeriods: 7, IncrementalOffset: null,
            PollingExpression: null, SourceExpression: null, Force: false));

        Assert.False(result.Created);
        var policy = (BasicRefreshPolicy)db.Model.Tables["Sales"].RefreshPolicy;
        Assert.Equal(7, policy.IncrementalPeriods);
        Assert.Equal(10, policy.RollingWindowPeriods);
        Assert.Equal(ValidSourceExpression, policy.SourceExpression);
    }

    [Fact]
    public void Set_Create_MissingRequiredOptions_Throws()
    {
        var db = BaseModel();
        var ex = Assert.Throws<ArgumentException>(() => new TomRefreshPolicyManager(db).Set(
            new RefreshPolicySetRequest(
                "Sales", null, null, RollingWindowPeriods: 10, null, null, null, null, null, Force: false)));

        Assert.Contains("--rolling-window-granularity", ex.Message);
        Assert.Contains("--source-expression", ex.Message);
    }

    [Fact]
    public void Set_SourceExpressionWithoutRangeRefs_ThrowsValidation()
    {
        var db = BaseModel();
        var ex = Assert.Throws<RefreshPolicyValidationException>(() => new TomRefreshPolicyManager(db).Set(
            CreateRequest() with { SourceExpression = "let Source = Src in Source" }));

        Assert.Contains(ex.Issues, i => i.Code == "source_expression_range_refs" && i.IsError);
    }

    [Fact]
    public void Set_IncrementalCoarserThanRollingWindow_ThrowsValidation()
    {
        var db = BaseModel();
        var ex = Assert.Throws<RefreshPolicyValidationException>(() => new TomRefreshPolicyManager(db).Set(
            CreateRequest() with { RollingWindowGranularity = "day", IncrementalGranularity = "year" }));

        Assert.Contains(ex.Issues, i => i.Code == "granularity_order");
    }

    [Fact]
    public void Set_HybridBelowCompat1565_ThrowsValidation()
    {
        var db = BaseModel(compatibilityLevel: 1500);
        var ex = Assert.Throws<RefreshPolicyValidationException>(() => new TomRefreshPolicyManager(db).Set(
            CreateRequest() with { Mode = "hybrid" }));

        Assert.Contains(ex.Issues, i => i.Code == "hybrid_compat_level");
    }

    [Fact]
    public void Set_MalformedRangeParameter_ThrowsValidation()
    {
        var db = BaseModel();
        db.Model.Expressions.Add(new NamedExpression
        {
            Name = "RangeStart",
            Kind = ExpressionKind.M,
            Expression = "1 meta [IsParameterQuery=false]"
        });

        var ex = Assert.Throws<RefreshPolicyValidationException>(
            () => new TomRefreshPolicyManager(db).Set(CreateRequest()));

        Assert.Contains(ex.Issues, i => i.Code == "range_parameter_meta");
    }

    [Fact]
    public void Set_Force_BypassesValidationErrors_ButKeepsIssues()
    {
        var db = BaseModel();
        var result = new TomRefreshPolicyManager(db).Set(
            CreateRequest() with { SourceExpression = "no range refs here", Force = true });

        Assert.NotNull(db.Model.Tables["Sales"].RefreshPolicy);
        Assert.Contains(result.Policy.Issues, i => i.Code == "source_expression_range_refs");
    }

    [Fact]
    public void Set_HappyPath_WarnsAboutMissingPollingExpression()
    {
        var db = BaseModel();
        var result = new TomRefreshPolicyManager(db).Set(CreateRequest());

        var warning = Assert.Single(result.Policy.Issues, i => i.Code == "no_polling_expression");
        Assert.False(warning.IsError);
    }

    [Fact]
    public void Set_InvalidModeOrGranularity_ThrowsArgument()
    {
        var db = BaseModel();
        var manager = new TomRefreshPolicyManager(db);

        Assert.Throws<ArgumentException>(() => manager.Set(CreateRequest() with { Mode = "te2" }));
        Assert.Throws<ArgumentException>(() => manager.Set(CreateRequest() with { RollingWindowGranularity = "fortnight" }));
    }

    [Fact]
    public void Set_UnknownTable_ThrowsNotFound()
    {
        var db = BaseModel();
        Assert.Throws<ObjectNotFoundException>(
            () => new TomRefreshPolicyManager(db).Set(CreateRequest() with { Table = "Bogus" }));
    }

    [Fact]
    public void Get_ReturnsNull_WhenNoPolicy()
    {
        var db = BaseModel();
        Assert.Null(new TomRefreshPolicyManager(db).Get("Sales"));
    }

    [Fact]
    public void Get_ReturnsPolicyWithIssues()
    {
        var db = BaseModel();
        var manager = new TomRefreshPolicyManager(db);
        manager.Set(CreateRequest());

        var info = manager.Get("Sales");

        Assert.NotNull(info);
        Assert.Equal("Sales", info!.Table);
        Assert.Equal("Year", info.RollingWindowGranularity);
        Assert.Equal(10, info.RollingWindowPeriods);
        Assert.Contains(info.Issues, i => i.Code == "no_polling_expression");
    }

    [Fact]
    public void Get_UnknownTable_ThrowsNotFound()
    {
        var db = BaseModel();
        Assert.Throws<ObjectNotFoundException>(() => new TomRefreshPolicyManager(db).Get("Bogus"));
    }

    [Fact]
    public void Remove_NullsPolicy()
    {
        var db = BaseModel();
        var manager = new TomRefreshPolicyManager(db);
        manager.Set(CreateRequest());

        var result = manager.Remove("Sales", ifExists: false);

        Assert.True(result.Changed);
        Assert.Null(db.Model.Tables["Sales"].RefreshPolicy);
    }

    [Fact]
    public void Remove_NoPolicy_IfExists_ReportsNotFound()
    {
        var db = BaseModel();
        var result = new TomRefreshPolicyManager(db).Remove("Sales", ifExists: true);

        Assert.False(result.Changed);
        Assert.Equal("not_found", result.Reason);
    }

    [Fact]
    public void Remove_NoPolicy_WithoutIfExists_ThrowsNotFound()
    {
        var db = BaseModel();
        Assert.Throws<RefreshPolicyNotFoundException>(
            () => new TomRefreshPolicyManager(db).Remove("Sales", ifExists: false));
    }

    [Fact]
    public void Summarize_ProducesCompactSummary()
    {
        var db = BaseModel();
        var manager = new TomRefreshPolicyManager(db);
        Assert.Equal("", TomRefreshPolicyManager.Summarize(db.Model.Tables["Sales"]));

        manager.Set(CreateRequest() with
        {
            IncrementalOffset = 1,
            PollingExpression = "let M = List.Max(Src[Modified]) in M"
        });

        Assert.Equal(
            "Import: keep 10 Year, refresh 3 Day, offset 1, detect changes",
            TomRefreshPolicyManager.Summarize(db.Model.Tables["Sales"]));
    }

    [Fact]
    public async Task Policy_RoundTripsThroughTmdlAndBim()
    {
        var db = BaseModel();
        new TomRefreshPolicyManager(db).Set(CreateRequest());

        var temp = Directory.CreateTempSubdirectory("tomix-refresh-policy-rt");
        try
        {
            var tmdlDir = Path.Combine(temp.FullName, "tmdl");
            var bimPath = Path.Combine(temp.FullName, "model.bim");

            await TomModelExporter.ExportAsync(
                db, new ModelExportRequest(tmdlDir, "tmdl", Force: true, SupportingFiles: false), CancellationToken.None);
            await TomModelExporter.ExportAsync(
                db, new ModelExportRequest(bimPath, "bim", Force: true, SupportingFiles: false), CancellationToken.None);

            var fromTmdl = TmdlSerializer.DeserializeDatabaseFromFolder(tmdlDir);
            var fromBim = TabularJsonSerializer.DeserializeDatabase(
                File.ReadAllText(bimPath), new DeserializeOptions(), CompatibilityMode.PowerBI);

            foreach (var reloaded in new[] { fromTmdl, fromBim })
            {
                var policy = Assert.IsType<BasicRefreshPolicy>(reloaded.Model.Tables["Sales"].RefreshPolicy);
                Assert.Equal(RefreshGranularityType.Year, policy.RollingWindowGranularity);
                Assert.Equal(10, policy.RollingWindowPeriods);
                Assert.Equal(3, policy.IncrementalPeriods);
                Assert.NotNull(reloaded.Model.Expressions.Find("RangeStart"));
                Assert.NotNull(reloaded.Model.Expressions.Find("RangeEnd"));
            }
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    private static RefreshPolicySetRequest CreateRequest() => new(
        "Sales",
        Mode: null,
        RollingWindowGranularity: "year",
        RollingWindowPeriods: 10,
        IncrementalGranularity: "day",
        IncrementalPeriods: 3,
        IncrementalOffset: null,
        PollingExpression: null,
        SourceExpression: ValidSourceExpression,
        Force: false);

    private static void AddRangeParameters(Database db)
    {
        foreach (var name in new[] { "RangeStart", "RangeEnd" })
            db.Model.Expressions.Add(new NamedExpression
            {
                Name = name,
                Kind = ExpressionKind.M,
                Expression = "#datetime(2024, 1, 1, 0, 0, 0) meta [IsParameterQuery=true, Type=\"DateTime\", IsParameterQueryRequired=true]"
            });
    }

    private static Database BaseModel(int compatibilityLevel = 1702)
    {
        var db = new Database
        {
            Name = "M",
            ID = "M",
            CompatibilityLevel = compatibilityLevel,
            Model = new Model { Name = "Model" }
        };

        var sales = new Table { Name = "Sales" };
        sales.Columns.Add(new DataColumn { Name = "Amount", DataType = DataType.Double, SourceColumn = "Amount" });
        sales.Partitions.Add(new Partition
        {
            Name = "Sales",
            Mode = ModeType.Import,
            Source = new MPartitionSource { Expression = "let Source = #table({}, {}) in Source" }
        });
        db.Model.Tables.Add(sales);

        return db;
    }
}
