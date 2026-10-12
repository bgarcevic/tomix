using System.Text.Json.Nodes;
using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Provider.Tom;
using TomJson = Microsoft.AnalysisServices.Tabular.JsonSerializer;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// <c>get --output-format tmdl/bim/tmsl</c> (#243): fragments come from TOM's own serializers, so
/// they must address the snapshot object they were asked for and carry everything a model file
/// would. The round trips paste a fragment back into a model folder and compare the object TOM
/// reads from it with the one it reads from the untouched model.
/// </summary>
public sealed class TomObjectScripterTests
{
    /// <summary>Every snapshot object, of every kind, scripts in both formats as itself.</summary>
    [Fact]
    public void EverySnapshotObject_Scripts_AsItself()
    {
        var db = Fixture();
        var objects = Flatten(TomModelSummarizer.Snapshot(db, "kitchen").Objects).ToList();

        var uncovered = Enum.GetValues<ModelObjectKind>()
            .Where(kind => kind != ModelObjectKind.Model && objects.All(o => o.Kind != kind))
            .ToList();
        Assert.True(uncovered.Count == 0, $"Fixture has no object of kind: {string.Join(", ", uncovered)}. Add one.");

        foreach (var obj in objects)
        {
            var tmdl = TomObjectScripter.Script(db, obj.Path, obj.Kind, ObjectScriptFormat.Tmdl);
            var json = TomObjectScripter.Script(db, obj.Path, obj.Kind, ObjectScriptFormat.Json);

            Assert.False(string.IsNullOrWhiteSpace(tmdl), $"{obj.Kind} {obj.Path}: empty TMDL");
            Assert.IsType<JsonObject>(JsonNode.Parse(json));
            // Unnamed kinds (a KPI, a refresh policy) and relationships (named by ID) carry no
            // name of their own to check.
            if (obj.Kind is not (ModelObjectKind.Kpi or ModelObjectKind.RefreshPolicy or ModelObjectKind.Relationship))
            {
                Assert.True(json.Contains(obj.Name, StringComparison.Ordinal), $"{obj.Kind} {obj.Path}: JSON is another object:\n{json}");
                Assert.True(tmdl.Contains(obj.Name, StringComparison.Ordinal), $"{obj.Kind} {obj.Path}: TMDL is another object:\n{tmdl}");
            }
        }
    }

    [Fact]
    public void TableChild_Tmdl_IsWrappedInRefTable_WithEveryProperty()
    {
        var tmdl = TomObjectScripter.Script(Fixture(), "driftSales/driftTotal", ModelObjectKind.Measure, ObjectScriptFormat.Tmdl);

        Assert.StartsWith("ref table driftSales", tmdl);
        Assert.Contains("\t/// drift measure description", tmdl);
        Assert.Contains("\tmeasure driftTotal = SUM(driftSales[driftAmount])", tmdl);
        Assert.Contains("formatString: #,0 \"drift\"", tmdl);
        Assert.Contains("displayFolder: driftFolder", tmdl);
        Assert.Contains("kpi", tmdl);
        Assert.Contains("annotation MeasureTag", tmdl);
    }

    [Fact]
    public void ModelRoot_ScriptsTheModel()
    {
        var db = Fixture();

        var json = JsonNode.Parse(TomObjectScripter.Script(db, ".", ModelObjectKind.Model, ObjectScriptFormat.Json))!;
        var tmdl = TomObjectScripter.Script(db, ".", ModelObjectKind.Model, ObjectScriptFormat.Tmdl);

        Assert.Equal("drift model description", json["description"]!.GetValue<string>());
        Assert.Equal(db.Model.Tables.Count, json["tables"]!.AsArray().Count);
        Assert.Contains("model Model", tmdl);
    }

    /// <summary>Mutation paths know columns, not calculated columns; a same-named partition makes the kind matter.</summary>
    [Fact]
    public void CalculatedColumn_ResolvesAsAColumn()
    {
        var db = Fixture();
        db.Model.Tables["driftSales"].Partitions.Add(new Partition
        {
            Name = "driftCalc",
            Source = new MPartitionSource { Expression = "let x = 1 in x" }
        });

        var json = TomObjectScripter.Script(db, "driftSales/driftCalc", ModelObjectKind.CalculatedColumn, ObjectScriptFormat.Json);

        Assert.Equal("COUNTROWS(driftSales)", JsonNode.Parse(json)!["expression"]!.GetValue<string>());
    }

    [Fact]
    public void MissingObject_ThrowsObjectNotFound()
        => Assert.Throws<ObjectNotFoundException>(
            () => TomObjectScripter.Script(Fixture(), "driftSales/nope", ModelObjectKind.Measure, ObjectScriptFormat.Tmdl));

    public static TheoryData<string, ModelObjectKind> RoundTripCases => new()
    {
        { "driftSales/driftTotal", ModelObjectKind.Measure },
        { "driftSales/driftNote", ModelObjectKind.Column },
        { "driftSales/driftCalc", ModelObjectKind.CalculatedColumn },
        { "driftSales/driftPartition", ModelObjectKind.Partition },
        { "driftSales/driftHierarchy", ModelObjectKind.Hierarchy },
        { "driftSales/driftTotal/KPI", ModelObjectKind.Kpi },
        { "Relationships/driftRel", ModelObjectKind.Relationship },
        { "driftRole", ModelObjectKind.Role },
        { "driftFn", ModelObjectKind.Function },
    };

    /// <summary>
    /// A TMDL fragment pasted into its own file of a model folder that lacks the object reads back
    /// as the same object: nothing the model file carries is lost.
    /// </summary>
    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void TmdlFragment_PastedIntoTheModel_ReadsBackTheSameObject(string path, ModelObjectKind kind)
    {
        using var dir = new TempDir();
        var db = Fixture();
        var expected = Serialize(ReadFolder(db, dir.Combine("full")), path, kind);

        var fragment = TomObjectScripter.Script(db, path, kind, ObjectScriptFormat.Tmdl);
        Detach(Find(db, path, kind));
        var folder = dir.Combine("pasted");
        TmdlSerializer.SerializeDatabaseToFolder(db, folder);
        File.WriteAllText(Path.Combine(folder, "fragment.tmdl"), fragment);

        Assert.Equal(expected, Serialize(TmdlSerializer.DeserializeDatabaseFromFolder(folder), path, kind));
    }

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void JsonFragment_IsTheObjectAsTheBimCarriesIt(string path, ModelObjectKind kind)
    {
        var db = Fixture();

        var json = TomObjectScripter.Script(db, path, kind, ObjectScriptFormat.Json);

        var bim = JsonNode.Parse(TomJson.SerializeDatabase(db, new SerializeOptions()))!;
        Assert.Contains(JsonNode.Parse(json)!, Descendants(bim), JsonNodeComparer.Instance);
    }

    /// <summary>The searchable-agreement kitchen sink, plus the kinds it lacks and a column nothing depends on.</summary>
    private static Database Fixture()
    {
        var db = CatalogSearchableAgreementTests.KitchenSink();
        var sales = db.Model.Tables["driftSales"];
        sales.Columns.Add(new DataColumn
        {
            Name = "driftNote",
            DataType = DataType.String,
            SourceColumn = "Note",
            Description = "drift note description",
            DisplayFolder = "driftFolder",
            IsHidden = true,
            LineageTag = "6f1c2a77-5d1e-4c55-9d4e-2b8f0f0c1a11"
        });
        sales.Calendars.Add(new Calendar { Name = "driftCalendar" });
        db.Model.Cultures.Add(new Culture { Name = "da-DK" });
        db.Model.Relationships[0].Name = "driftRel";
        return db;
    }

    private static Database ReadFolder(Database db, string folder)
    {
        TmdlSerializer.SerializeDatabaseToFolder(db, folder);
        return TmdlSerializer.DeserializeDatabaseFromFolder(folder);
    }

    private static string Serialize(Database db, string path, ModelObjectKind kind)
        => TomJson.SerializeObject(Find(db, path, kind));

    private static MetadataObject Find(Database db, string path, ModelObjectKind kind)
    {
        var table = path.Split('/')[0];
        return kind switch
        {
            ModelObjectKind.Measure => db.Model.Tables[table].Measures[path.Split('/')[1]],
            ModelObjectKind.Column or ModelObjectKind.CalculatedColumn => db.Model.Tables[table].Columns[path.Split('/')[1]],
            ModelObjectKind.Partition => db.Model.Tables[table].Partitions[path.Split('/')[1]],
            ModelObjectKind.Hierarchy => db.Model.Tables[table].Hierarchies[path.Split('/')[1]],
            ModelObjectKind.Kpi => db.Model.Tables[table].Measures[path.Split('/')[1]].KPI,
            ModelObjectKind.Relationship => db.Model.Relationships[path.Split('/')[1]],
            ModelObjectKind.Role => db.Model.Roles[path],
            ModelObjectKind.Function => db.Model.Functions[path],
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }

    private static void Detach(MetadataObject obj)
    {
        switch (obj)
        {
            case Measure measure: measure.Table.Measures.Remove(measure); break;
            case Column column: column.Table.Columns.Remove(column); break;
            case Partition partition: partition.Table.Partitions.Remove(partition); break;
            case Hierarchy hierarchy: hierarchy.Table.Hierarchies.Remove(hierarchy); break;
            case KPI kpi: kpi.Measure.KPI = null; break;
            case Relationship relationship: relationship.Model.Relationships.Remove(relationship); break;
            case ModelRole role: role.Model.Roles.Remove(role); break;
            case Function function: function.Model.Functions.Remove(function); break;
            default: throw new ArgumentOutOfRangeException(nameof(obj), obj.ObjectType, null);
        }
    }

    private static IEnumerable<ModelObject> Flatten(IEnumerable<ModelObject> objects)
    {
        foreach (var obj in objects)
        {
            yield return obj;
            foreach (var child in Flatten(obj.Children))
                yield return child;
        }
    }

    private static IEnumerable<JsonNode> Descendants(JsonNode node)
    {
        yield return node;
        var children = node switch
        {
            JsonObject o => o.Select(p => p.Value),
            JsonArray a => a.AsEnumerable(),
            _ => []
        };
        foreach (var child in children.OfType<JsonNode>())
            foreach (var descendant in Descendants(child))
                yield return descendant;
    }

    private sealed class JsonNodeComparer : IEqualityComparer<JsonNode>
    {
        public static readonly JsonNodeComparer Instance = new();

        public bool Equals(JsonNode? x, JsonNode? y) => JsonNode.DeepEquals(x, y);

        public int GetHashCode(JsonNode obj) => 0;
    }
}
