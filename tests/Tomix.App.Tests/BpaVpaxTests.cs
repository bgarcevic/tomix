using Tomix.App.Bpa;
using Tomix.App.Vertipaq;
using Tomix.Core.Bpa;
using Tomix.Core.Models;
using Tomix.Core.Results;
using Tomix.Core.Rules;
using Tomix.Core.Vertipaq;
using Tomix.Provider.Tmdl;

namespace Tomix.App.Tests;

/// <summary>
/// #234: <c>bpa run --vpax</c> reads VertiPaq statistics from an exported file and overlays them
/// on the analyzed snapshot as <c>Vertipaq_*</c> annotations, so statistics rules run on a model
/// that was never annotated, without writing anything to it.
/// </summary>
public sealed class BpaVpaxTests
{
    private const string RiRuleJson =
        "[{\"ID\":\"RI\",\"Name\":\"ri\",\"Category\":\"c\",\"Severity\":2,\"Scope\":\"Relationship\","
        + "\"Expression\":\"Convert.ToInt64(GetAnnotation(\\\"Vertipaq_RIViolationInvalidRows\\\")) > 0\",\"CompatibilityLevel\":1200}]";

    [Fact]
    public async Task Overlay_AnnotatesMatchingTablesColumnsAndRelationships()
    {
        var snapshot = await SampleSnapshotAsync();

        var overlay = VertipaqSnapshotOverlay.Apply(snapshot, SampleStats());

        var sales = Find(overlay.Snapshot, ModelObjectKind.Table, "Sales");
        Assert.Equal("5000", sales.Property("Annotation:Vertipaq_RowCount"));
        var amount = sales.Children.Single(c => c.Name == "Amount");
        Assert.Equal("4000", amount.Property("Annotation:Vertipaq_Cardinality"));
        var relationship = AllObjects(overlay.Snapshot)
            .Single(o => o.Kind == ModelObjectKind.Relationship && o.Property("ToTable") == "Customers");
        Assert.Equal("7", relationship.Property("Annotation:Vertipaq_RIViolationInvalidRows"));
        Assert.Equal("100000", overlay.Snapshot.Properties!["Annotation:Vertipaq_TotalSize"]);
        Assert.Empty(overlay.UnmatchedTables);
        Assert.Equal(3, overlay.MatchedTables);
    }

    [Fact]
    public async Task Overlay_KeepsTheModelsOwnPropertiesAndNamesTablesNotInTheModel()
    {
        var snapshot = await SampleSnapshotAsync();
        var stats = SampleStats() with
        {
            Tables = [.. SampleStats().Tables, Table("Returns", rows: 10, size: 10)]
        };

        var overlay = VertipaqSnapshotOverlay.Apply(snapshot, stats);

        Assert.Equal(["Returns"], overlay.UnmatchedTables);
        var before = Find(snapshot, ModelObjectKind.Table, "Sales");
        var after = Find(overlay.Snapshot, ModelObjectKind.Table, "Sales");
        Assert.All(before.Properties!, p => Assert.Equal(p.Value, after.Property(p.Key)));
        // The source snapshot is not mutated.
        Assert.Null(before.Property("Annotation:Vertipaq_RowCount"));
    }

    [Fact]
    public async Task Run_WithVpax_EvaluatesStatisticsRulesInsteadOfSkippingThem()
    {
        using var config = new TempConfigDir();
        using var root = new TempDir();
        var model = SampleModel.CopyTo(root, "model");
        var rules = root.WriteFile("rules.json", RiRuleJson);

        var without = await RunAsync(config, model, new StubAnalyzer(SampleStats()), r => r with { RulesFiles = [rules], NoDefaults = true });
        var with = await RunAsync(config, model, new StubAnalyzer(SampleStats()), r => r with { RulesFiles = [rules], NoDefaults = true, VpaxPath = "stats.vpax" });

        Assert.Equal("RI", Assert.Single(without.Data!.MissingVertipaqStatsRules).RuleId);
        Assert.True(with.Success, string.Join("; ", with.Diagnostics.Select(d => d.Message)));
        Assert.Empty(with.Data!.MissingVertipaqStatsRules);
        var violation = Assert.Single(with.Data.Violations);
        Assert.Equal("RI", violation.RuleId);
        Assert.Equal("Relationships/rel-customers", violation.ObjectPath);
    }

    [Fact]
    public async Task Run_WithVpax_LeavesTheModelFilesUntouched()
    {
        using var config = new TempConfigDir();
        using var root = new TempDir();
        var model = SampleModel.CopyTo(root, "model");
        var before = HashFiles(model);

        var result = await RunAsync(config, model, new StubAnalyzer(SampleStats()), r => r with { VpaxPath = "stats.vpax", Ruleset = "full" });

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(before, HashFiles(model));
    }

    [Fact]
    public async Task Run_VpaxMatchingNoTable_FailsInsteadOfPassingOnZeros()
    {
        // Obfuscated or foreign statistics would set the model-level annotations and let every
        // per-object comparison read 0 — the silent pass #266 guards against.
        using var config = new TempConfigDir();
        using var root = new TempDir();
        var model = SampleModel.CopyTo(root, "model");
        var stats = SampleStats() with { Tables = [Table("Other", 1, 1)], Columns = [], Relationships = [] };

        var result = await RunAsync(config, model, new StubAnalyzer(stats), r => r with { VpaxPath = "other.vpax" });

        Assert.False(result.Success);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("TOMIX_BPA_VPAX_MISMATCH", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task Run_VpaxWithTablesNotInTheModel_WarnsAndStillRuns()
    {
        using var config = new TempConfigDir();
        using var root = new TempDir();
        var model = SampleModel.CopyTo(root, "model");
        var rules = root.WriteFile("rules.json", RiRuleJson);
        var stats = SampleStats() with { Tables = [.. SampleStats().Tables, Table("Returns", 1, 1)] };

        var result = await RunAsync(config, model, new StubAnalyzer(stats), r => r with { RulesFiles = [rules], NoDefaults = true, VpaxPath = "stats.vpax" });

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal("TOMIX_BPA_VPAX_PARTIAL", warning.Code);
        Assert.Contains("Returns", warning.Message);
        Assert.Single(result.Data!.Violations);
    }

    [Theory]
    [InlineData(null, false, new[] { "standard" })]
    [InlineData(null, true, new[] { "standard", "vertipaq" })]
    [InlineData(" ", true, new[] { "standard", "vertipaq" })]
    [InlineData("standard", true, new[] { "standard" })]
    [InlineData("full", true, new[] { "full" })]
    public void ResolveRulesets_AddsVertipaqOnlyToTheImplicitDefaultWhenStatisticsExist(
        string? ruleset, bool hasStats, string[] expected)
        => Assert.Equal(expected, BpaRuleLoader.ResolveRulesets(ruleset, hasStats));

    [Theory]
    [InlineData(null, true)]
    [InlineData("standard", false)]
    public async Task Run_WithVpaxAndNoRuleset_AlsoLoadsTheVertipaqPreset(string? ruleset, bool loadsVertipaq)
    {
        using var config = new TempConfigDir();
        using var root = new TempDir();
        var model = SampleModel.CopyTo(root, "model");

        var result = await RunAsync(config, model, new StubAnalyzer(SampleStats()), r => r with { VpaxPath = "stats.vpax", Ruleset = ruleset });

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(loadsVertipaq, result.Data!.RuleSources.Any(s => s.Name == "vertipaq"));
        Assert.Equal(loadsVertipaq, result.Data.Violations.Any(v => v.RuleId == "FIX_REFERENTIAL_INTEGRITY_VIOLATIONS"));
    }

    [Fact]
    public async Task Run_WithoutStatisticsOrRuleset_LoadsOnlyStandard()
    {
        using var config = new TempConfigDir();
        using var root = new TempDir();
        var model = SampleModel.CopyTo(root, "model");

        var result = await RunAsync(config, model, new StubAnalyzer(SampleStats()), r => r);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(["standard"], result.Data!.RuleSources.Where(s => s.Origin == BpaRuleOrigin.Ruleset).Select(s => s.Name));
        Assert.Empty(result.Data.MissingVertipaqStatsRules);
    }

    [Fact]
    public async Task Run_UnreadableVpax_FailsWithReadError()
    {
        using var config = new TempConfigDir();
        using var root = new TempDir();
        var model = SampleModel.CopyTo(root, "model");
        var analyzer = new StubAnalyzer(SampleStats())
        {
            ImportError = new VertipaqAnalysisException(VertipaqAnalysisKind.VpaxReadFailed, "not a vpax")
        };

        var result = await RunAsync(config, model, analyzer, r => r with { VpaxPath = "broken.vpax" });

        Assert.False(result.Success);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("TOMIX_VPAX_READ_FAILED", Assert.Single(result.Diagnostics).Code);
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("full")]
    public void VertipaqCategory_IsDefaultOff(string preset)
    {
        var rules = BpaRuleLoader.SelectBundled(BpaRuleLoader.LoadBundledCatalog(), preset)!;

        Assert.DoesNotContain(rules, r => r.Category == "VertiPaq");
    }

    [Fact]
    public void VertipaqPreset_HoldsEveryBundledStatisticsRule()
    {
        // Most runs have no statistics, so a statistics rule anywhere else would only ever be
        // listed as not checked: every bundled rule that reads them sits in the preset.
        var catalog = BpaRuleLoader.LoadBundledCatalog();
        var rules = BpaRuleLoader.SelectBundled(catalog, "vertipaq")!;

        Assert.Equal(
            [
                "AVOID_BI-DIRECTIONAL_RELATIONSHIPS_AGAINST_HIGH-CARDINALITY_COLUMNS",
                "FIX_REFERENTIAL_INTEGRITY_VIOLATIONS",
                "LARGE_SHARE_OF_MODEL_SIZE",
                "LARGE_TABLES_SHOULD_BE_PARTITIONED",
                "UNUSED_HIDDEN_HIGH_CARDINALITY_COLUMNS"
            ],
            rules.Select(r => r.Id).Order(StringComparer.Ordinal));
        Assert.Equal(
            rules.Select(r => r.Id).Order(StringComparer.Ordinal),
            catalog.Where(r => BpaEngine.ReadsVertipaqStats(r.Expression ?? "")).Select(r => r.Id).Order(StringComparer.Ordinal));
        Assert.Contains("vertipaq", BpaRuleLoader.KnownRulesets);
    }

    [Fact]
    public void LargeShareRule_FlagsATableHoldingMostOfALargeModel()
    {
        var snapshot = StatsSnapshot(
            totalSize: 200_000_000,
            Tbl("Big", 150_000_000),
            Tbl("Small", 50_000_000));

        var result = Evaluate(snapshot, "LARGE_SHARE_OF_MODEL_SIZE");

        Assert.Equal(["Big"], result.Violations.Select(v => v.ObjectPath));
        Assert.Equal(0, result.RuleErrors);
    }

    [Theory]
    [InlineData(50_000_000L, 40_000_000L)] // below the size floor: small models are fine
    [InlineData(200_000_000L, 100_000_000L)] // exactly half is not "most"
    public void LargeShareRule_IgnoresSmallModelsAndBalancedTables(long total, long big)
    {
        var snapshot = StatsSnapshot(total, Tbl("Big", big), Tbl("Rest", total - big));

        var result = Evaluate(snapshot, "LARGE_SHARE_OF_MODEL_SIZE");

        Assert.Empty(result.Violations);
        Assert.Equal(0, result.RuleErrors);
    }

    [Fact]
    public void HiddenHighCardinalityRule_FlagsOnlyUnusedHiddenColumns()
    {
        var table = Tbl("Fact", 10,
            Col("Fact", "HiddenId", hidden: true, cardinality: 5_000_000),
            Col("Fact", "VisibleId", hidden: false, cardinality: 5_000_000),
            Col("Fact", "HiddenFlag", hidden: true, cardinality: 2),
            Col("Fact", "HiddenKey", hidden: true, cardinality: 5_000_000));
        var dim = Tbl("Dim", 10, Col("Dim", "Key", hidden: false, cardinality: 5_000_000));
        var relationship = new ModelObject("r", ModelObjectKind.Relationship, "Relationships/r",
            null, null, null, false, null, [],
            new Dictionary<string, string>
            {
                ["FromTable"] = "Fact",
                ["FromColumn"] = "'Fact'[HiddenKey]",
                ["ToTable"] = "Dim",
                ["ToColumn"] = "'Dim'[Key]",
                ["IsActive"] = "true"
            });
        var snapshot = new ModelSnapshot("M", 1601, [table, dim, relationship],
            new Dictionary<string, string> { ["Annotation:Vertipaq_TotalSize"] = "20" });

        var result = Evaluate(snapshot, "UNUSED_HIDDEN_HIGH_CARDINALITY_COLUMNS");

        Assert.Equal(["Fact/HiddenId"], result.Violations.Select(v => v.ObjectPath));
        Assert.Equal(0, result.RuleErrors);
    }

    private static BpaRunResult Evaluate(ModelSnapshot snapshot, string ruleId)
    {
        var rules = BpaRuleLoader.SelectBundled(BpaRuleLoader.LoadBundledCatalog(), "vertipaq")!
            .Where(r => r.Id == ruleId)
            .ToList();
        Assert.Single(rules);
        return new BpaEngine().Evaluate(snapshot, new BpaEngineOptions(rules));
    }

    private static ModelSnapshot StatsSnapshot(long totalSize, params ModelObject[] tables)
        => new("M", 1601, tables,
            new Dictionary<string, string> { ["Annotation:Vertipaq_TotalSize"] = totalSize.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    private static ModelObject Tbl(string name, long size, params ModelObject[] columns)
        => new(name, ModelObjectKind.Table, name, null, null, null, false, null, columns,
            new Dictionary<string, string>
            {
                ["ObjectType"] = "Table",
                ["Annotation:Vertipaq_TableSize"] = size.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });

    private static ModelObject Col(string table, string name, bool hidden, long cardinality)
        => new(name, ModelObjectKind.Column, $"{table}/{name}", null, null, null, hidden, name, [],
            new Dictionary<string, string>
            {
                ["ObjectType"] = "DataColumn",
                ["Annotation:Vertipaq_Cardinality"] = cardinality.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });

    private static async Task<ModelSnapshot> SampleSnapshotAsync()
    {
        await using var session = await new TmdlModelProvider()
            .OpenAsync(new ModelReference(SampleModel.Locate()), CancellationToken.None);
        return await session.GetSnapshotAsync(CancellationToken.None);
    }

    private static ModelObject Find(ModelSnapshot snapshot, ModelObjectKind kind, string name)
        => AllObjects(snapshot).Single(o => o.Kind == kind && o.Name == name);

    private static IEnumerable<ModelObject> AllObjects(ModelSnapshot snapshot)
    {
        return snapshot.Objects.SelectMany(Walk);

        static IEnumerable<ModelObject> Walk(ModelObject obj) => [obj, .. obj.Children.SelectMany(Walk)];
    }

    private static VertipaqModelStats SampleStats()
        => new(
            "basic", null, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            TotalSize: 100_000, TableCount: 3, ColumnCount: 2, MaxRowCount: 5000,
            Tables: [Table("Sales", 5000, 90_000), Table("Customers", 100, 5_000), Table("Products", 50, 5_000)],
            Columns: [Column("Sales", "Amount", 4000), Column("Sales", "CustomerID", 90)],
            Relationships:
            [
                new VertipaqRelationshipStats(
                    "'Sales'[CustomerID] -> 'Customers'[CustomerID]",
                    "Sales", "Customers", "'Sales'[CustomerID]", "'Customers'[CustomerID]",
                    24, 90, 100, 3, 7, 0.1, true, "OneDirection")
            ],
            Partitions: []);

    private static VertipaqTableStats Table(string name, long rows, long size)
        => new(name, rows, size, size, size, 0, 0, 0, 0, 0, 1, 1, 1, true);

    private static VertipaqColumnStats Column(string table, string name, long cardinality)
        => new(table, name, cardinality, "Int64", "HASH", 100, 60, 40, 0,
            1, 1, null, 1, 1, false, true, false, "Ready");

    private static Dictionary<string, string> HashFiles(string folder)
        => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(
                f => Path.GetRelativePath(folder, f),
                f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));

    private static async Task<TomixResult<BpaRunResult>> RunAsync(
        TempConfigDir config, string model, IVertipaqAnalyzer analyzer, Func<BpaRunRequest, BpaRunRequest> configure)
        => await new BpaRunHandler(
                [new TmdlModelProvider()], config.Stores, new BpaUserRuleState(config.Path), config.Path,
                vertipaq: analyzer)
            .HandleAsync(configure(new BpaRunRequest(new ModelReference(model))), CancellationToken.None);

    private sealed class StubAnalyzer(VertipaqModelStats stats) : IVertipaqAnalyzer
    {
        public Exception? ImportError { get; init; }

        public Task<VertipaqModelStats> AnalyzeAsync(ModelReference model, CancellationToken _)
            => throw new NotSupportedException();

        public Task<VertipaqModelStats> ImportAsync(string vpaxPath, CancellationToken _)
            => ImportError is null ? Task.FromResult(stats) : Task.FromException<VertipaqModelStats>(ImportError);

        public Task<VertipaqExportResult> ExportAsync(ModelReference model, string vpaxPath, bool obfuscate, CancellationToken _)
            => throw new NotSupportedException();
    }
}
