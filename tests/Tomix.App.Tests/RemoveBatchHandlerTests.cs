using Microsoft.AnalysisServices.Tabular;
using Tomix.App.Models;
using Tomix.App.Mutations;
using Tomix.App.Rm;
using Tomix.App.Tests.Support;
using Tomix.Core.Models;
using Tomix.Core.Results;
using Tomix.Provider.Tmdl;
using Tomix.Provider.Tom;

namespace Tomix.App.Tests;

/// <summary>
/// Batch <c>rm</c> against a real TMDL model holding one of every removable kind: targets resolve
/// before anything changes, the guards judge the whole deletion set (references between targets
/// never block, whatever the order; references and cascades onto survivors do), and the batch
/// lands as one mutation.
/// </summary>
public sealed class RemoveBatchHandlerTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;
    private readonly TempDir _dir = new();
    private readonly TempConfigDir _config = new();

    public RemoveBatchHandlerTests() => WriteFixture(_dir.Path).GetAwaiter().GetResult();

    public void Dispose()
    {
        _config.Dispose();
        _dir.Dispose();
    }

    // Every kind rm removes, addressed the way 'tx ls' prints it (relationships by endpoints).
    [Theory]
    [InlineData("Product", "table")]
    [InlineData("Sales/Derived", "measure")]
    [InlineData("Sales/Spare", "column")]
    [InlineData("Sales/H2", "hierarchy")]
    [InlineData("Sales/Geo/L2", "level")]
    [InlineData("Sales/P2", "partition")]
    [InlineData("CG/Item1", "calculationitem")]
    [InlineData("Sales[Key] -> Product[Key]", "relationship")]
    [InlineData("Roles/Writer", "role")]
    [InlineData("Roles/Reader/user1@contoso.com", "member")]
    [InlineData("Perspectives/Persp1", "perspective")]
    [InlineData("Cultures/fr-FR", "culture")]
    [InlineData("Expressions/Expr1", "expression")]
    [InlineData("Functions/Func1", "function")]
    [InlineData("DataSources/DS1", "datasource")]
    [InlineData("Sales/Rev/KPI", "kpi")]
    [InlineData("Roles/Reader/Sales", "tablepermission")]
    [InlineData("Sales/Cal", "calendar")]
    public async Task Removes_every_kind_alongside_a_measure_in_one_save(string path, string type)
    {
        var before = await PathsAsync();
        var target = Assert.Single(before, p => p.Kind == KindOf(type) && Matches(p.Path, path, type)).Path;

        var result = await HandleAsync([path, "Sales/Lonely"], save: true, cascade: true);

        Assert.True(result.Success, Describe(result));
        Assert.Equal(MutationStatus.Saved, result.Data!.Status);
        var resolved = result.Data.Targets[0];
        Assert.Equal(type, resolved.Type);
        Assert.Equal(target, resolved.Path);
        Assert.Equal([target, "Sales/Lonely"], result.Data.Removed);
        var after = await PathsAsync();
        Assert.DoesNotContain(after, p => p.Path == target && p.Kind == KindOf(type));
        Assert.DoesNotContain(after, p => p.Path == "Sales/Lonely");
        Assert.Contains(after, p => p.Path == "Sales/Base");
    }

    [Theory]
    [InlineData("Sales/Base", "Sales/Derived")]
    [InlineData("Sales/Derived", "Sales/Base")]
    public async Task References_between_targets_do_not_block_in_either_order(string first, string second)
    {
        // Derived calls Base: removing both breaks nothing that survives.
        var result = await HandleAsync([first, second], save: true);

        Assert.True(result.Success, Describe(result));
        Assert.Null(result.Data!.BrokenReferences);
        Assert.Null(result.Data.Reason);
        Assert.DoesNotContain(await PathsAsync(), p => p.Path is "Sales/Base" or "Sales/Derived");
    }

    [Fact]
    public async Task A_reference_from_a_survivor_blocks_the_save_and_changes_nothing()
    {
        var files = Files();

        var result = await HandleAsync(["Sales/Base", "Sales/Lonely"], save: true);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_RM_BREAKS_REFS", result.Diagnostics[0].Code);
        Assert.Contains("Sales/Derived", result.Diagnostics[0].Message);
        Assert.Equal(files, Files());
    }

    [Fact]
    public async Task A_reference_from_a_survivor_previews_as_blocked_and_force_removes()
    {
        var preview = await HandleAsync(["Sales/Base", "Sales/Lonely"], save: false);
        Assert.True(preview.Success, Describe(preview));
        Assert.Equal(MutationStatus.Preview, preview.Data!.Status);
        Assert.Equal("would_block", preview.Data.Reason);
        Assert.Equal([RemoveBatchResult.BlockedByReferences], preview.Data.BlockedBy);
        Assert.Equal(["Sales/Derived"], preview.Data.BrokenReferences);
        Assert.Equal(["Sales/Base", "Sales/Lonely"], preview.Data.WouldRemove);

        var forced = await HandleAsync(["Sales/Base", "Sales/Lonely"], save: true, force: true);
        Assert.True(forced.Success, Describe(forced));
        Assert.Equal(["Sales/Derived"], forced.Data!.BrokenReferences);
        Assert.DoesNotContain(await PathsAsync(), p => p.Path == "Sales/Base");
    }

    [Fact]
    public async Task A_cascade_onto_a_survivor_needs_cascade()
    {
        var files = Files();

        var blocked = await HandleAsync(["Sales/Key", "Sales/Lonely"], save: true);
        Assert.False(blocked.Success);
        Assert.Equal("TOMIX_RM_NEEDS_CASCADE", blocked.Diagnostics[0].Code);
        Assert.Contains("relationship 'Sales'[Key] -> 'Product'[Key]", blocked.Diagnostics[0].Message);
        Assert.Equal(files, Files());

        var preview = await HandleAsync(["Sales/Key", "Sales/Lonely"], save: false);
        Assert.Equal([RemoveBatchResult.BlockedByCascade], preview.Data!.BlockedBy);
        var cascade = Assert.Single(preview.Data.Cascades!);
        Assert.Equal("Sales/Key", cascade.Target);
        Assert.Equal("relationship", cascade.Type);
        Assert.True(cascade.RequiresCascade);

        var allowed = await HandleAsync(["Sales/Key", "Sales/Lonely"], save: true, cascade: true);
        Assert.True(allowed.Success, Describe(allowed));
        Assert.DoesNotContain(await PathsAsync(), p => p.Kind == ModelObjectKind.Relationship);
    }

    [Theory]
    [InlineData("Sales/Key", "Sales[Key] -> Product[Key]")]
    [InlineData("Sales[Key] -> Product[Key]", "Sales/Key")]
    public async Task A_structural_reference_between_targets_is_no_cascade(string first, string second)
    {
        var result = await HandleAsync([first, second], save: true);

        Assert.True(result.Success, Describe(result));
        Assert.Null(result.Data!.Cascades);
        Assert.Equal(2, result.Data.Removed!.Count);
    }

    [Theory]
    [InlineData("Sales/SortKey", "Sales/Label")]
    [InlineData("Sales/Label", "Sales/SortKey")]
    public async Task A_sort_by_between_targets_is_no_cascade(string first, string second)
    {
        // Label sorts by SortKey; removing SortKey alone clears a survivor's sort-by.
        var alone = await HandleAsync(["Sales/SortKey", "Sales/Lonely"], save: false);
        Assert.Equal("sort-by on 'Sales'[Label] (cleared)", Assert.Single(alone.Data!.Cascades!).Description);

        var result = await HandleAsync([first, second], save: true);

        Assert.True(result.Success, Describe(result));
        Assert.Null(result.Data!.Cascades);
    }

    [Fact]
    public async Task Row_level_security_in_a_removed_role_does_not_block()
    {
        // Roles/Auditor filters Region in DAX: the filter breaks unless the role goes too.
        var alone = await HandleAsync(["Region", "Sales/Lonely"], save: false, cascade: true);
        Assert.Equal(["Roles/Auditor/Region"], alone.Data!.BrokenReferences);

        var result = await HandleAsync(["Region", "Roles/Auditor"], save: true);

        Assert.True(result.Success, Describe(result));
        Assert.Null(result.Data!.BrokenReferences);
        Assert.Null(result.Data.Cascades);
    }

    [Fact]
    public async Task Power_query_from_a_survivor_blocks_and_force_removes()
    {
        // Staging reads Server; Broken mentions Server but does not parse, so it counts too.
        var files = Files();

        var blocked = await HandleAsync(["Expressions/Server", "Sales/Lonely"], save: true);
        Assert.False(blocked.Success);
        Assert.Equal("TOMIX_RM_BREAKS_REFS", blocked.Diagnostics[0].Code);
        Assert.Contains("Power Query (M) reference(s) in: Expressions/Staging", blocked.Diagnostics[0].Message);
        Assert.Equal(files, Files());

        var preview = await HandleAsync(["Expressions/Server", "Sales/Lonely"], save: false);
        Assert.Equal([RemoveBatchResult.BlockedByMReferences], preview.Data!.BlockedBy);
        Assert.Equal(["Expressions/Broken", "Expressions/Staging"], preview.Data.BrokenMReferences!.Order());
        Assert.Null(preview.Data.BrokenReferences);

        var forced = await HandleAsync(["Expressions/Server", "Sales/Lonely"], save: true, force: true);
        Assert.True(forced.Success, Describe(forced));
        Assert.DoesNotContain(await PathsAsync(), p => p.Path == "Expressions/Server");
    }

    [Theory]
    [InlineData("Expressions/Staging", "Stage")]
    [InlineData("Stage", "Expressions/Staging")]
    public async Task Power_query_between_targets_does_not_block_in_either_order(string first, string second)
    {
        // Stage's partition reads Staging; alone, Staging would break it.
        var alone = await HandleAsync(["Expressions/Staging", "Sales/Lonely"], save: false);
        Assert.Equal(["Stage/Stage"], alone.Data!.BrokenMReferences);

        var result = await HandleAsync([first, second], save: true);

        Assert.True(result.Success, Describe(result));
        Assert.Null(result.Data!.BrokenMReferences);
    }

    [Fact]
    public async Task Field_access_and_local_bindings_are_not_power_query_references()
    {
        // Product's partition binds its own Shadowed and reads a [Shadowed] field: neither is the query.
        var result = await HandleAsync(["Expressions/Shadowed", "Sales/Lonely"], save: true);

        Assert.True(result.Success, Describe(result));
        Assert.Null(result.Data!.BrokenMReferences);
    }

    [Fact]
    public async Task Single_rm_guards_power_query_too()
    {
        var handler = new RemoveModelObjectHandler([new TmdlModelProvider()], _config.Stores);
        RemoveModelObjectRequest Single(bool save) => new(
            new ModelReference(_dir.Path), "Expressions/Staging", null, IfExists: false, save, SaveTo: null,
            Serialization: "", Force: false);

        var preview = await handler.HandleAsync(Single(save: false), None);
        Assert.Equal("would_block", preview.Data!.Reason);
        Assert.Equal(["Stage/Stage"], preview.Data.BrokenMReferences);
        Assert.Null(preview.Data.BrokenReferences);

        var saved = await handler.HandleAsync(Single(save: true), None);
        Assert.False(saved.Success);
        Assert.Equal("TOMIX_RM_BREAKS_REFS", saved.Diagnostics[0].Code);
        Assert.Contains("Stage/Stage", saved.Diagnostics[0].Message);
    }

    [Fact]
    public async Task A_lone_column_reference_is_judged_against_its_own_table()
    {
        // Legacy/IdText is FORMAT([Id]): it reads Legacy/Id, never Stage/Id of the same name.
        var other = await HandleAsync(["Stage/Id", "Sales/Lonely"], save: false);
        Assert.True(other.Success, Describe(other));
        Assert.Null(other.Data!.BrokenReferences);

        var own = await HandleAsync(["Legacy/Id", "Sales/Lonely"], save: false);
        Assert.True(own.Data is not null, Describe(own));
        Assert.Equal(["Legacy/IdText"], own.Data.BrokenReferences);
    }

    [Fact]
    public async Task A_target_inside_another_target_is_subsumed()
    {
        var result = await HandleAsync(["Sales/Amount", "Sales", "Sales/Rev/KPI"], save: false, cascade: true);

        Assert.True(result.Success, Describe(result));
        Assert.Equal(["Sales"], result.Data!.WouldRemove);
        Assert.All(result.Data.Targets.Where(t => t.Path != "Sales"), t =>
        {
            Assert.Equal(RemoveBatchTarget.Subsumed, t.Action);
            Assert.Equal("Sales", t.SubsumedBy);
        });
    }

    [Fact]
    public async Task Mixed_kinds_land_in_one_save()
    {
        var result = await HandleAsync(
            ["Functions/Func1", "Product", "Roles/Writer", "Sales/Derived", "CG/Item1", "Sales[Key] -> Product[Key]", "Cultures/fr-FR"],
            save: true);

        Assert.True(result.Success, Describe(result));
        Assert.Equal(
            ["function", "table", "role", "measure", "calculationitem", "relationship", "culture"],
            result.Data!.Targets.Select(t => t.Type));
        Assert.Null(result.Data.Cascades);
        var after = await PathsAsync();
        Assert.DoesNotContain(after, p => p.Path is "Functions/Func1" or "Product" or "Roles/Writer" or "Sales/Derived"
            or "CG/Item1" or "Cultures/fr-FR");
        Assert.DoesNotContain(after, p => p.Kind == ModelObjectKind.Relationship);
    }

    [Fact]
    public async Task Missing_targets_fail_the_whole_batch_and_are_all_named()
    {
        var files = Files();

        var result = await HandleAsync(["Sales/Lonely", "Sales/Nope", "Ghost"], save: true);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_OBJECT_NOT_FOUND", result.Diagnostics[0].Code);
        Assert.Contains("Sales/Nope", result.Diagnostics[0].Message);
        Assert.Contains("Ghost", result.Diagnostics[0].Message);
        Assert.Equal(files, Files());
    }

    [Fact]
    public async Task If_exists_skips_missing_targets()
    {
        var result = await HandleAsync(["Sales/Lonely", "Sales/Nope"], save: true, ifExists: true);

        Assert.True(result.Success, Describe(result));
        Assert.Equal(["Sales/Lonely"], result.Data!.Removed);
        Assert.Equal(RemoveBatchTarget.NotFound, result.Data.Targets[1].Action);
    }

    [Fact]
    public async Task If_exists_with_nothing_found_is_unchanged()
    {
        var result = await HandleAsync(["Sales/Nope", "Ghost"], save: true, ifExists: true);

        Assert.True(result.Success, Describe(result));
        Assert.Equal(MutationStatus.Unchanged, result.Data!.Status);
    }

    [Fact]
    public async Task An_ambiguous_target_fails_the_batch()
    {
        // A measure named like the Sales partition: 'Sales/Sales' then names both.
        await using (var session = await new TmdlModelProvider().OpenAsync(new ModelReference(_dir.Path), None))
        {
            var mutator = (IModelMutationSession)session;
            mutator.AddObject(new ModelObjectAddRequest("Sales/Sales", "Measure", "1", [], IfNotExists: false));
            await mutator.SaveAsync(null, "tmdl", overwrite: true, None);
        }

        var result = await HandleAsync(["Sales/Lonely", "Sales/Sales"], save: true);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_OBJECT_AMBIGUOUS", result.Diagnostics[0].Code);
        Assert.Contains("Sales/Sales", result.Diagnostics[0].Message);
    }

    [Fact]
    public async Task A_data_source_with_a_surviving_bound_partition_blocks_even_with_force()
    {
        var blocked = await HandleAsync(["DataSources/DS2", "Sales/Lonely"], save: true, force: true, cascade: true);
        Assert.False(blocked.Success);
        Assert.Contains("Legacy/Legacy", blocked.Diagnostics[0].Message);

        var preview = await HandleAsync(["DataSources/DS2", "Sales/Lonely"], save: false);
        Assert.Equal([RemoveBatchResult.BlockedByBoundPartitions], preview.Data!.BlockedBy);
        Assert.Equal(["Legacy/Legacy"], preview.Data.BoundPartitions);

        // The partition's table goes too, so nothing that survives is bound to the source.
        var together = await HandleAsync(["DataSources/DS2", "Legacy"], save: true);
        Assert.True(together.Success, Describe(together));
        Assert.DoesNotContain(await PathsAsync(), p => p.Path is "DataSources/DS2" or "Legacy");
    }

    [Fact]
    public async Task A_live_session_applies_the_batch_as_one_undoable_step()
    {
        await using var session = await new TmdlModelProvider().OpenLiveAsync(new ModelReference(_dir.Path), None);
        var source = new LiveSessionSource(session, new LiveLeaseOptions("shell"));
        var handler = new RemoveBatchHandler(source, _config.Stores);

        var result = await handler.HandleAsync(Request(["Sales/Base", "Sales/Derived", "Functions/Func1"]), None);

        Assert.True(result.Success, Describe(result));
        Assert.Equal(MutationStatus.Applied, result.Data!.Status);
        Assert.Single(session.History);
        Assert.Null(await FindAsync(session, "Sales/Base"));

        await session.UndoAsync(null, None);
        Assert.NotNull(await FindAsync(session, "Sales/Base"));
        Assert.NotNull(await FindAsync(session, "Sales/Derived"));
        Assert.NotNull(await FindAsync(session, "Functions/Func1"));
    }

    private Task<TomixResult<RemoveBatchResult>> HandleAsync(
        IReadOnlyList<string> paths, bool save, bool force = false, bool cascade = false, bool ifExists = false)
        => new RemoveBatchHandler([new TmdlModelProvider()], _config.Stores).HandleAsync(
            Request(paths) with { Save = save, Force = force, Cascade = cascade, IfExists = ifExists }, None);

    private RemoveBatchRequest Request(IReadOnlyList<string> paths)
        => new(new ModelReference(_dir.Path), paths.Select(p => new RemoveTarget(p)).ToList(),
            IfExists: false, Save: false, SaveTo: null, Serialization: "", Force: false, Cascade: false);

    private static string Describe<T>(TomixResult<T> result)
        => string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"));

    private static ModelObjectKind KindOf(string type)
        => ModelObjectTypeCatalog.TryParse(type, out var kind) ? kind : throw new ArgumentException(type);

    private static bool Matches(string snapshotPath, string input, string type)
        => type == "relationship" || snapshotPath == input;

    private async Task<List<(string Path, ModelObjectKind Kind)>> PathsAsync()
    {
        await using var session = await new TmdlModelProvider().OpenAsync(new ModelReference(_dir.Path), None);
        var snapshot = await session.GetSnapshotAsync(None);
        return ModelObjects.ModelObjectProjection.Flatten(snapshot).Select(o => (o.Path, o.Kind)).ToList();
    }

    private static async Task<ModelObject?> FindAsync(ILiveModelSession session, string path)
    {
        var snapshot = await session.GetLiveSnapshotAsync(None);
        return ModelObjects.ModelObjectProjection.Flatten(snapshot.Snapshot).FirstOrDefault(o => o.Path == path);
    }

    private SortedDictionary<string, string> Files()
        => new(Directory.EnumerateFiles(_dir.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(_dir.Path, f), File.ReadAllText), StringComparer.Ordinal);

    /// <summary>One of every removable kind, plus measures that call each other (Derived calls
    /// Base), a relationship on Sales[Key], a column sorted by another (Label by SortKey), a role
    /// filtering Region in DAX, a table whose partition is bound to DS2, and shared expressions
    /// read from M: Staging reads Server, Stage's partition reads Staging, Broken (unparsable)
    /// mentions Server, and Product's partition shadows Shadowed.</summary>
    private static async Task WriteFixture(string path)
    {
        var db = new Database { Name = "M", ID = "M", CompatibilityLevel = 1702, Model = new Model { Name = "Model" } };
        var sales = new Table { Name = "Sales" };
        var amount = new DataColumn { Name = "Amount", DataType = DataType.Double, SourceColumn = "Amount" };
        sales.Columns.Add(amount);
        sales.Columns.Add(new DataColumn { Name = "Key", DataType = DataType.Double, SourceColumn = "Key" });
        sales.Columns.Add(new DataColumn { Name = "Spare", DataType = DataType.String, SourceColumn = "Spare" });
        var sortKey = new DataColumn { Name = "SortKey", DataType = DataType.Int64, SourceColumn = "SortKey" };
        sales.Columns.Add(sortKey);
        sales.Columns.Add(new DataColumn { Name = "Label", DataType = DataType.String, SourceColumn = "Label", SortByColumn = sortKey });
        sales.Partitions.Add(MPartition("Sales"));
        sales.Measures.Add(new Measure { Name = "Rev", Expression = "1" });
        sales.Measures.Add(new Measure { Name = "Base", Expression = "SUM ( Sales[Amount] )" });
        sales.Measures.Add(new Measure { Name = "Derived", Expression = "[Base] * 2" });
        sales.Measures.Add(new Measure { Name = "Lonely", Expression = "1" });
        var geo = new Hierarchy { Name = "Geo" };
        geo.Levels.Add(new Level { Name = "Lvl", Ordinal = 0, Column = amount });
        sales.Hierarchies.Add(geo);
        db.Model.Tables.Add(sales);

        var product = new Table { Name = "Product" };
        product.Columns.Add(new DataColumn { Name = "Key", DataType = DataType.Double, SourceColumn = "Key" });
        product.Partitions.Add(MPartition("Product",
            "let Shadowed = #table({\"Shadowed\"}, {{1}}) in Table.AddColumn(Shadowed, \"Copy\", each [Shadowed])"));
        db.Model.Tables.Add(product);

        var cg = new Table { Name = "CG", CalculationGroup = new CalculationGroup() };
        cg.Columns.Add(new DataColumn { Name = "Name", DataType = DataType.String, SourceColumn = "Name" });
        db.Model.Tables.Add(cg);

        var stage = new Table { Name = "Stage" };
        stage.Columns.Add(new DataColumn { Name = "Id", DataType = DataType.Int64, SourceColumn = "Id" });
        stage.Partitions.Add(MPartition("Stage", "let Source = Staging in Source"));
        db.Model.Tables.Add(stage);

        var region = new Table { Name = "Region" };
        region.Columns.Add(new DataColumn { Name = "Code", DataType = DataType.String, SourceColumn = "Code" });
        region.Partitions.Add(MPartition("Region"));
        db.Model.Tables.Add(region);

        db.Model.Roles.Add(new ModelRole { Name = "Reader", ModelPermission = ModelPermission.Read });
        var auditor = new ModelRole { Name = "Auditor", ModelPermission = ModelPermission.Read };
        auditor.TablePermissions.Add(new TablePermission { Table = region, FilterExpression = "Region[Code] <> \"X\"" });
        db.Model.Roles.Add(auditor);

        var mutator = new TomModelMutator(db);
        foreach (var (target, type, value) in new (string, string, string?)[]
        {
            ("Sales/H2", "Hierarchy", null),
            ("Sales/Geo/L2", "Level", "Spare"),
            ("Sales/Cal", "Calendar", null),
            ("CG/Item1", "CalcItem", "SELECTEDMEASURE()"),
            ("Sales/Rev", "KPI", "0"),
            ("Sales/P2", "Partition", "let Source = #table({},{}) in Source"),
            ("Expr1", "Expression", "1 meta [IsParameterQuery=false]"),
            ("Server", "Expression", "\"sql.contoso.com\" meta [IsParameterQuery=true, Type=\"Text\"]"),
            ("Staging", "Expression", "let Source = Sql.Database(Server, \"staging\") in Source"),
            ("Broken", "Expression", "let Source = Server,"),
            ("Shadowed", "Expression", "1"),
            ("Func1", "Function", "() => 1"),
            ("Persp1", "Perspective", null),
            ("fr-FR", "Culture", null),
            ("DS1", "ProviderDataSource", "Data Source=localhost;Initial Catalog=db"),
            ("DS2", "ProviderDataSource", "Data Source=localhost;Initial Catalog=legacy"),
            ("Writer", "Role", null),
            ("Reader/Sales", "TablePermission", "TRUE()"),
            ("Reader/user1@contoso.com", "Member", null),
            ("Sales[Key]->Product[Key]", "Relationship", null),
        })
        {
            Assert.True(mutator.AddObject(new ModelObjectAddRequest(target, type, value, [], IfNotExists: false)).Changed, target);
        }

        var legacy = new Table { Name = "Legacy" };
        legacy.Columns.Add(new DataColumn { Name = "Id", DataType = DataType.Int64, SourceColumn = "Id" });
        // A lone [Id] is Legacy's own column, not Stage's (which comes first in the model).
        legacy.Columns.Add(new CalculatedColumn { Name = "IdText", DataType = DataType.String, Expression = "FORMAT ( [Id], \"0\" )" });
        legacy.Partitions.Add(new Partition
        {
            Name = "Legacy",
            Mode = ModeType.Import,
            Source = new QueryPartitionSource { DataSource = db.Model.DataSources["DS2"], Query = "SELECT 1 AS Id" }
        });
        db.Model.Tables.Add(legacy);

        await TomModelExporter.ExportAsync(
            db, new ModelExportRequest(path, "tmdl", Overwrite: true, SupportingFiles: false), None);
    }

    private static Partition MPartition(string name, string expression = "let Source = #table({}, {}) in Source") => new()
    {
        Name = name,
        Mode = ModeType.Import,
        Source = new MPartitionSource { Expression = expression }
    };
}
