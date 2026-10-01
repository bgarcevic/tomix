using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Properties;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// The two test oracles of ADR 0002 §2 and ADR 0003 for <see cref="TomChangeJournal"/>:
/// <list type="bullet">
/// <item><see cref="AssertEventsMatchDiff"/>: the changes a transaction commits agree with the
/// ID-keyed diff of the snapshots before and after it. A write that bypasses
/// <see cref="TomWriter"/> shows up in the diff with no change to explain it.</item>
/// <item><see cref="AssertRollbackRestores"/>: applying a mutation and rolling it back leaves the
/// model as it was: byte-identical TMDL and the same IDs for a swapped checkpoint, the same
/// content for a <c>CopyTo</c> one.</item>
/// </list>
/// </summary>
internal static class JournalOracle
{
    /// <summary>Runs <paramref name="mutate"/> in a journal transaction, checks its changes
    /// against the snapshot diff, and returns them.</summary>
    public static IReadOnlyList<ModelChange> AssertEventsMatchDiff(Database database, Action<TomModelMutator> mutate)
    {
        var journal = new TomChangeJournal(database);
        var before = TomModelSummarizer.Snapshot(journal.Database, "M", journal.Ids);
        var modelId = journal.Ids.GetOrAdd(journal.Database.Model);

        journal.Begin();
        mutate(new TomModelMutator(journal.Database, journal.Writer));
        var changes = journal.Commit();

        var after = TomModelSummarizer.Snapshot(journal.Database, "M", journal.Ids);
        var diff = SnapshotDiff.Compute(before, after, modelId);
        var byId = changes.ToDictionary(c => c.Id);
        Assert.True(byId.Count == changes.Count, $"More than one change per object:\n{Describe(changes)}");

        foreach (var expected in diff)
        {
            Assert.True(byId.TryGetValue(expected.Id, out var actual),
                $"The snapshot shows {Describe(expected)} but the journal reported nothing for it. A write bypassed TomWriter?\n"
                + $"Journal changes:\n{Describe(changes)}");
            Assert.True(actual.Change == expected.Change && actual.Path == expected.Path && actual.OldPath == expected.OldPath,
                $"The snapshot shows {Describe(expected)} but the journal reported {Describe(actual)}.");
        }

        var diffIds = diff.Select(d => d.Id).ToHashSet();
        foreach (var change in changes.Where(c => c.Change != ModelChangeKind.Modified))
            Assert.True(diffIds.Contains(change.Id),
                $"The journal reported {Describe(change)} but the snapshots show no such change.");

        return changes;
    }

    /// <summary>Applies <paramref name="mutate"/> in a transaction, rolls it back and checks the
    /// model and its IDs are back where they started.</summary>
    public static void AssertRollbackRestores(Database database, Action<TomModelMutator> mutate, TomCheckpointRestore restore)
    {
        var journal = new TomChangeJournal(database, restore);
        var tmdlBefore = TmdlSerializer.SerializeDatabase(journal.Database);
        var before = TomModelSummarizer.Snapshot(journal.Database, "M", journal.Ids);

        journal.Begin();
        mutate(new TomModelMutator(journal.Database, journal.Writer));
        Assert.NotEqual(tmdlBefore, TmdlSerializer.SerializeDatabase(journal.Database));
        journal.Rollback();

        var tmdlAfter = TmdlSerializer.SerializeDatabase(journal.Database);
        var after = TomModelSummarizer.Snapshot(journal.Database, "M", journal.Ids);
        if (restore == TomCheckpointRestore.Swap)
        {
            Assert.Equal(tmdlBefore, tmdlAfter);
            Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
        }
        else
        {
            // CopyTo re-creates changed objects at the end of their collections: same content and
            // IDs, sibling order free.
            Assert.Equal(SortedLines(tmdlBefore), SortedLines(tmdlAfter));
            Assert.Equal(SnapshotDiff.Flatten(before), SnapshotDiff.Flatten(after));
        }

        Assert.Equal(0, journal.Depth);
        Assert.Empty(journal.Entries);
    }

    private static List<string> SortedLines(string text)
        => text.Split('\n').Select(l => l.TrimEnd('\r')).Order(StringComparer.Ordinal).ToList();

    private static string Describe(ModelChange change)
        => $"{change.Change} {change.ObjectKind} {change.Id} '{change.Path}'"
           + (change.OldPath is null ? "" : $" (was '{change.OldPath}')")
           + (change.Properties is null ? "" : $" [{string.Join(", ", change.Properties)}]");

    private static string Describe(IEnumerable<ModelChange> changes)
        => string.Join("\n", changes.Select(c => "  " + Describe(c))) is { Length: > 0 } text ? text : "  (none)";
}

/// <summary>
/// The changes between two snapshots of one live session, keyed by object ID, in the shape a
/// <c>model.changed</c> event uses. Fields derived from other objects (a relationship's label from
/// its columns, a table's RLS and perspective roll-ups, a level's column name) are left out: the
/// journal reports the object written, not every object whose read-out mentions it.
/// </summary>
internal static class SnapshotDiff
{
    private static readonly Dictionary<ModelObjectKind, string[]> DerivedProperties = new()
    {
        [ModelObjectKind.Relationship] = ["FromColumn", "ToColumn", "FromTable", "ToTable"],
        [ModelObjectKind.Table] = ["TableHasRls", "RowLevelSecurity", "Perspectives", "TableIsCalc", "RefreshPolicy",
            PropertyBagKeys.RefreshPolicySourceExpression, PropertyBagKeys.RefreshPolicyPollingExpression],
        [ModelObjectKind.Column] = ["UsedInRelationships", "UsedInHierarchies", "UsedInVariations", "TableDataCategory", "AlternateOf"],
        [ModelObjectKind.CalculatedColumn] = ["UsedInRelationships", "UsedInHierarchies", "UsedInVariations", "TableDataCategory", "AlternateOf"],
        [ModelObjectKind.Role] = [PropertyBagKeys.RlsExpression],
        [ModelObjectKind.Partition] = ["DataSourceName", "DataSourceType", "QueryGroup"],
        [ModelObjectKind.Measure] = [PropertyBagKeys.KpiTargetExpression, PropertyBagKeys.KpiStatusExpression, PropertyBagKeys.KpiTrendExpression],
    };

    // Detail is a read-out of other fields for these kinds: level count, column name, mode.
    private static readonly HashSet<ModelObjectKind> DerivedDetail =
        [ModelObjectKind.Table, ModelObjectKind.Hierarchy, ModelObjectKind.Level, ModelObjectKind.Relationship];

    public static List<ModelChange> Compute(ModelSnapshot before, ModelSnapshot after, ObjectId modelId)
    {
        var old = Index(before, modelId);
        var @new = Index(after, modelId);
        var cultures = old.Values.Where(n => n.Node.Kind == ModelObjectKind.Culture).Select(n => n.Node.Name)
            .Intersect(@new.Values.Where(n => n.Node.Kind == ModelObjectKind.Culture).Select(n => n.Node.Name))
            .ToHashSet(StringComparer.Ordinal);

        // Columns renamed in the transaction: a sort-by naming one follows it without a write.
        var renamedColumns = @new.Values
            .Where(e => e.Node.Kind is ModelObjectKind.Column or ModelObjectKind.CalculatedColumn
                        && old.TryGetValue(e.Id, out var o) && o.Node.Name != e.Node.Name)
            .Select(e => (old[e.Id].Node.Name, e.Node.Name))
            .ToHashSet();

        var changes = new List<ModelChange>();
        foreach (var (id, entry) in @new)
        {
            if (old.TryGetValue(id, out var previous))
            {
                if (Compare(previous, entry, old, @new, cultures, renamedColumns) is { } change)
                    changes.Add(change);
            }
            else if (entry.Parent is not { } parent || old.ContainsKey(parent))
            {
                changes.Add(new ModelChange(id, entry.Node.Kind, ModelChangeKind.Added, entry.Node.Path));
            }
        }

        foreach (var (id, entry) in old)
        {
            if (!@new.ContainsKey(id) && (entry.Parent is not { } parent || @new.ContainsKey(parent)))
                changes.Add(new ModelChange(id, entry.Node.Kind, ModelChangeKind.Removed, entry.Node.Path));
        }

        return changes;
    }

    /// <summary>Every object's own fields by ID, for order-insensitive comparison.</summary>
    public static SortedDictionary<ObjectId, string> Flatten(ModelSnapshot snapshot)
        => new(Index(snapshot, modelId: null).ToDictionary(
            p => p.Key,
            p => JsonSerializer.Serialize(Own(p.Value.Node) with
            {
                Properties = p.Value.Node.Properties?.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .ToDictionary(kv => kv.Key, kv => kv.Value)
            })));

    private static ModelChange? Compare(Entry before, Entry after, Dictionary<ObjectId, Entry> old,
        Dictionary<ObjectId, Entry> @new, HashSet<string> cultures, HashSet<(string, string)> renamedColumns)
    {
        var kind = after.Node.Kind;
        var fields = ChangedFields(before.Node, after.Node, cultures);
        if (fields.Contains("SortByColumn")
            && renamedColumns.Contains((before.Node.Property("SortByColumn") ?? "", after.Node.Property("SortByColumn") ?? "")))
            fields.Remove("SortByColumn");
        if (before.Parent != after.Parent)
            return new ModelChange(after.Id, kind, ModelChangeKind.Moved, after.Node.Path, OldPath: before.Node.Path);

        if (!string.Equals(Segment(before, old), Segment(after, @new), StringComparison.Ordinal))
            return new ModelChange(after.Id, kind, ModelChangeKind.Renamed, after.Node.Path, OldPath: before.Node.Path);

        return fields.Count > 0
            ? new ModelChange(after.Id, kind, ModelChangeKind.Modified, after.Node.Path, Properties: fields)
            : null;
    }

    private static List<string> ChangedFields(ModelObject before, ModelObject after, HashSet<string> cultures)
    {
        var fields = new List<string>();
        var kind = after.Kind;
        if (kind != ModelObjectKind.Relationship && before.Name != after.Name) fields.Add("Name");
        if (!DerivedDetail.Contains(kind) && before.Detail != after.Detail) fields.Add("Detail");
        if (before.Expression != after.Expression) fields.Add("Expression");
        if (before.Description != after.Description) fields.Add("Description");
        if (before.Hidden != after.Hidden) fields.Add("Hidden");
        if (before.SourceColumn != after.SourceColumn) fields.Add("SourceColumn");
        if (JsonSerializer.Serialize(before.PolicyInfo) != JsonSerializer.Serialize(after.PolicyInfo)) fields.Add("PolicyInfo");

        var derived = DerivedProperties.GetValueOrDefault(kind) ?? [];
        var keys = (before.Properties?.Keys ?? []).Union(after.Properties?.Keys ?? []);
        foreach (var key in keys)
        {
            if (derived.Contains(key) || IsDerivedTranslation(key, cultures))
                continue;
            if (before.Property(key) != after.Property(key))
                fields.Add(key);
        }

        return fields;
    }

    // A translation key names its culture, so renaming or removing a culture changes the keys of
    // every translated object; the journal reports the culture.
    private static bool IsDerivedTranslation(string key, HashSet<string> cultures)
        => key.StartsWith(PropertyBagKeys.TranslationPrefix, StringComparison.Ordinal)
           && key[PropertyBagKeys.TranslationPrefix.Length..] is var rest
           && rest.LastIndexOf('/') is var slash and > 0
           && !cultures.Contains(rest[..slash]);

    private static string Segment(Entry entry, Dictionary<ObjectId, Entry> index)
        => entry.Parent is { } parent && index[parent].Node.Kind != ModelObjectKind.Model
            ? entry.Node.Path[(index[parent].Node.Path.Length + 1)..]
            : entry.Node.Path;

    private static ModelObject Own(ModelObject node) => node with { Children = [] };

    private static Dictionary<ObjectId, Entry> Index(ModelSnapshot snapshot, ObjectId? modelId)
    {
        var index = new Dictionary<ObjectId, Entry>();
        if (modelId is { } root)
        {
            // The model root is not a snapshot object; its fields are the snapshot's own.
            index[root] = new Entry(root, null, new ModelObject(snapshot.Name, ModelObjectKind.Model, TomObjectTree.ModelPath,
                Detail: snapshot.CompatibilityLevel.ToString(), Expression: null, snapshot.Description, Hidden: false,
                SourceColumn: null, Children: [], snapshot.Properties));
        }

        void Visit(ModelObject node, ObjectId? parent)
        {
            var id = node.Id ?? throw new InvalidOperationException($"'{node.Path}' has no ID.");
            index.Add(id, new Entry(id, parent ?? modelId, node));
            foreach (var child in node.Children)
                Visit(child, id);
        }

        foreach (var node in snapshot.Objects)
            Visit(node, null);

        return index;
    }

    private sealed record Entry(ObjectId Id, ObjectId? Parent, ModelObject Node);
}

/// <summary>A model with one of everything the mutators touch, wired so removals cascade.</summary>
internal static class JournalFixture
{
    public static Database Rich()
    {
        // 1702: DAX functions (1702) and calendars (1701).
        var db = TestModels.NewDatabase(compatibilityLevel: 1702);
        var model = db.Model;
        model.Culture = "en-US";
        model.Annotations.Add(new Annotation { Name = "Team", Value = "BI" });

        var sales = TestModels.NewTable("Sales", "CustomerId", "Amount", "MonthName", "MonthNo");
        sales.Partitions.Add(new Partition
        {
            Name = "Sales 2023",
            Source = new MPartitionSource { Expression = "let Source = #table({}, {}) in Source" }
        });
        sales.Columns["MonthName"].SortByColumn = sales.Columns["MonthNo"];
        sales.Columns.Add(new CalculatedColumn { Name = "Double", DataType = DataType.Int64, Expression = "[Amount] * 2" });
        var revenue = new Measure
        {
            Name = "Revenue",
            Expression = "SUM(Sales[Amount])",
            KPI = new KPI { TargetExpression = "100", StatusExpression = "1" }
        };
        revenue.Annotations.Add(new Annotation { Name = "Owner", Value = "finance" });
        sales.Measures.Add(revenue);
        sales.Measures.Add(new Measure { Name = "Count", Expression = "COUNTROWS(Sales)" });
        var calendarHierarchy = new Hierarchy { Name = "Calendar" };
        calendarHierarchy.Levels.Add(new Level { Name = "MonthName", Ordinal = 0, Column = sales.Columns["MonthName"] });
        calendarHierarchy.Levels.Add(new Level { Name = "MonthNo", Ordinal = 1, Column = sales.Columns["MonthNo"] });
        sales.Hierarchies.Add(calendarHierarchy);
        sales.Calendars.Add(new Calendar { Name = "Fiscal" });

        var customer = TestModels.NewTable("Customer", "Id", "Name");
        var metrics = TestModels.NewTable("Metrics");
        metrics.Measures.Add(new Measure { Name = "Margin", Expression = "1" });

        var timeIntelligence = new Table { Name = "Time Intelligence", CalculationGroup = new CalculationGroup { Precedence = 1 } };
        timeIntelligence.Columns.Add(new DataColumn { Name = "Name", DataType = DataType.String, SourceColumn = "Name" });
        timeIntelligence.Partitions.Add(new Partition { Name = "Time Intelligence", Source = new CalculationGroupSource() });
        timeIntelligence.CalculationGroup.CalculationItems.Add(new CalculationItem { Name = "Current", Expression = "SELECTEDMEASURE()", Ordinal = 0 });
        timeIntelligence.CalculationGroup.CalculationItems.Add(new CalculationItem { Name = "YTD", Expression = "SELECTEDMEASURE() * 2", Ordinal = 1 });

        foreach (var table in new[] { sales, customer, metrics, timeIntelligence })
            model.Tables.Add(table);

        var relationship = new SingleColumnRelationship
        {
            Name = "SalesToCustomer",
            FromColumn = sales.Columns["CustomerId"],
            ToColumn = customer.Columns["Id"],
            FromCardinality = RelationshipEndCardinality.Many,
            ToCardinality = RelationshipEndCardinality.One
        };
        model.Relationships.Add(relationship);
        sales.Columns["CustomerId"].Variations.Add(new Variation
        {
            Name = "Variation",
            Relationship = relationship,
            DefaultColumn = customer.Columns["Id"]
        });

        var role = new ModelRole { Name = "Readers", ModelPermission = ModelPermission.Read };
        role.Members.Add(new ExternalModelRoleMember
        {
            MemberName = "user@contoso.com",
            IdentityProvider = "AzureAD",
            MemberType = RoleMemberType.User
        });
        var permission = new TablePermission { Table = customer, FilterExpression = "[Id] > 0" };
        permission.ColumnPermissions.Add(new ColumnPermission { Column = customer.Columns["Name"], MetadataPermission = MetadataPermission.None });
        role.TablePermissions.Add(permission);
        model.Roles.Add(role);

        var perspective = new Perspective { Name = "Reporting" };
        var perspectiveSales = new PerspectiveTable { Table = sales };
        perspectiveSales.PerspectiveColumns.Add(new PerspectiveColumn { Column = sales.Columns["Amount"] });
        perspectiveSales.PerspectiveMeasures.Add(new PerspectiveMeasure { Measure = revenue });
        perspectiveSales.PerspectiveHierarchies.Add(new PerspectiveHierarchy { Hierarchy = calendarHierarchy });
        perspective.PerspectiveTables.Add(perspectiveSales);
        model.Perspectives.Add(perspective);

        var culture = new Culture { Name = "da-DK" };
        model.Cultures.Add(culture);
        culture.ObjectTranslations.Add(new ObjectTranslation { Object = sales, Property = TranslatedProperty.Caption, Value = "Salg" });
        culture.ObjectTranslations.Add(new ObjectTranslation { Object = sales.Columns["Amount"], Property = TranslatedProperty.Caption, Value = "Beløb" });
        culture.ObjectTranslations.Add(new ObjectTranslation { Object = revenue, Property = TranslatedProperty.Caption, Value = "Omsætning" });
        culture.ObjectTranslations.Add(new ObjectTranslation { Object = calendarHierarchy.Levels["MonthNo"], Property = TranslatedProperty.Caption, Value = "Månednr" });

        model.Expressions.Add(new NamedExpression
        {
            Name = "Environment",
            Kind = ExpressionKind.M,
            Expression = "\"prod\" meta [IsParameterQuery=true, Type=\"Text\", IsParameterQueryRequired=true]"
        });
        model.Functions.Add(new Function { Name = "AddOne", Expression = "(x) => x + 1" });
        model.DataSources.Add(new ProviderDataSource { Name = "Warehouse", ConnectionString = "Data Source=localhost" });
        return db;
    }
}
