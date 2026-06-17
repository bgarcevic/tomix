using Tomix.Core.Models;
using Tomix.Provider.Tom;
using Microsoft.AnalysisServices.Tabular;
using CompatibilityMode = Microsoft.AnalysisServices.CompatibilityMode;
using TabularJsonSerializer = Microsoft.AnalysisServices.Tabular.JsonSerializer;

namespace Tomix.App.Tests;

/// <summary>
/// Proves that every object type advertised by <c>tx add --type</c> is created by the real
/// <see cref="TomModelMutator"/> and survives a round-trip through BOTH serializations: the model
/// is saved to a TMDL folder and to a .bim file (the production export path) and reopened, and the
/// new object must be present in each reload.
/// </summary>
public sealed class AddObjectRoundTripTests
{
    // The full advertised --type list from AddCommand.
    public static IEnumerable<object[]> AdvertisedTypes() =>
    [
        ["Table"], ["CalcTable"], ["CalcGroup"], ["Measure"], ["CalcColumn"], ["DataColumn"],
        ["Hierarchy"], ["Level"], ["Calendar"], ["CalcItem"], ["KPI"], ["Partition"],
        ["MPartition"], ["EntityPartition"], ["PolicyRangePartition"], ["Expression"], ["Function"],
        ["Perspective"], ["Culture"], ["ProviderDataSource"], ["StructuredDataSource"], ["Role"],
        ["TablePermission"], ["Member"]
    ];

    [Theory]
    [MemberData(nameof(AdvertisedTypes))]
    public async Task AddObject_CreatesAndRoundTripsThroughTmdlAndBim(string type)
    {
        var (path, value) = TargetFor(type);

        var db = BaseModel();
        var mutator = new TomModelMutator(db);

        var result = mutator.AddObject(new ModelObjectAddRequest(
            path, type, value, [], IfNotExists: false));
        Assert.True(result.Changed, $"AddObject for '{type}' should report Changed=true.");

        var temp = Directory.CreateTempSubdirectory("tomix-add-rt");
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

            Assert.True(Exists(fromTmdl, type), $"'{type}' missing after TMDL round-trip.");
            Assert.True(Exists(fromBim, type), $"'{type}' missing after .bim round-trip.");
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public void AddObject_UnknownType_Throws()
    {
        var db = BaseModel();
        var mutator = new TomModelMutator(db);

        Assert.Throws<NotSupportedException>(() => mutator.AddObject(new ModelObjectAddRequest(
            "Sales/Whatever", "Bogus", null, [], IfNotExists: false)));
    }

    // The new-object path + primary value used per type. Names are unique so existence checks are clean.
    private static (string Path, string? Value) TargetFor(string type) => type switch
    {
        "Table" => ("Dim", null),
        "CalcTable" => ("CalcT", "{1}"),
        "CalcGroup" => ("CG2", null),
        "Measure" => ("Sales/M2", "1"),
        "CalcColumn" => ("Sales/CC", "Sales[Amount]"),
        "DataColumn" => ("Sales/DC", "DC"),
        "Hierarchy" => ("Sales/H2", null),
        "Level" => ("Sales/Geo/L2", "Amount"),
        "Calendar" => ("Sales/Cal", null),
        "CalcItem" => ("CG/Item1", "SELECTEDMEASURE()"),
        "KPI" => ("Sales/Rev", "0"),
        "Partition" => ("Sales/P2", "let Source = #table({},{}) in Source"),
        "MPartition" => ("Sales/MP2", "let Source = #table({},{}) in Source"),
        "EntityPartition" => ("Sales/EP", "Orders"),
        "PolicyRangePartition" => ("Sales/PR", null),
        "Expression" => ("Expr1", "1 meta [IsParameterQuery=false]"),
        "Function" => ("Func1", "() => 1"),
        "Perspective" => ("Persp1", null),
        "Culture" => ("fr-FR", null),
        "ProviderDataSource" => ("DS1", "Data Source=localhost;Initial Catalog=db"),
        "StructuredDataSource" => ("DS2", null),
        "Role" => ("Writer", null),
        "TablePermission" => ("Reader/Sales", "Sales[Amount] > 0"),
        "Member" => ("Reader/user1@contoso.com", null),
        _ => throw new InvalidOperationException($"No target defined for {type}")
    };

    private static bool Exists(Database db, string type)
    {
        var model = db.Model;
        var sales = model.Tables.FirstOrDefault(t => t.Name == "Sales");
        var cg = model.Tables.FirstOrDefault(t => t.Name == "CG");
        var reader = model.Roles.FirstOrDefault(r => r.Name == "Reader");

        return type switch
        {
            "Table" => model.Tables.Any(t => t.Name == "Dim"),
            "CalcTable" => model.Tables.Any(t => t.Name == "CalcT"
                && t.Partitions.Any(p => p.Source is CalculatedPartitionSource)),
            "CalcGroup" => model.Tables.Any(t => t.Name == "CG2" && t.CalculationGroup is not null),
            "Measure" => sales!.Measures.Any(m => m.Name == "M2"),
            "CalcColumn" => sales!.Columns.Any(c => c.Name == "CC" && c is CalculatedColumn),
            "DataColumn" => sales!.Columns.Any(c => c.Name == "DC" && c is DataColumn),
            "Hierarchy" => sales!.Hierarchies.Any(h => h.Name == "H2"),
            "Level" => sales!.Hierarchies.Single(h => h.Name == "Geo").Levels.Any(l => l.Name == "L2"),
            "Calendar" => sales!.Calendars.Any(c => c.Name == "Cal"),
            "CalcItem" => cg!.CalculationGroup!.CalculationItems.Any(i => i.Name == "Item1"),
            "KPI" => sales!.Measures.Single(m => m.Name == "Rev").KPI is not null,
            "Partition" => sales!.Partitions.Any(p => p.Name == "P2"),
            "MPartition" => sales!.Partitions.Any(p => p.Name == "MP2" && p.Source is MPartitionSource),
            "EntityPartition" => sales!.Partitions.Any(p => p.Name == "EP"
                && p.Source is EntityPartitionSource e && e.EntityName == "Orders"),
            "PolicyRangePartition" => sales!.Partitions.Any(p => p.Name == "PR"
                && p.Source is PolicyRangePartitionSource),
            "Expression" => model.Expressions.Any(e => e.Name == "Expr1"),
            "Function" => model.Functions.Any(f => f.Name == "Func1"),
            "Perspective" => model.Perspectives.Any(p => p.Name == "Persp1"),
            "Culture" => model.Cultures.Any(c => c.Name == "fr-FR"),
            "ProviderDataSource" => model.DataSources.Any(d => d.Name == "DS1" && d is ProviderDataSource),
            "StructuredDataSource" => model.DataSources.Any(d => d.Name == "DS2" && d is StructuredDataSource),
            "Role" => model.Roles.Any(r => r.Name == "Writer"),
            "TablePermission" => reader!.TablePermissions.Any(p => p.Name == "Sales"),
            "Member" => reader!.Members.Any(m => m.MemberName == "user1@contoso.com"),
            _ => throw new InvalidOperationException($"No existence check defined for {type}")
        };
    }

    private static Database BaseModel()
    {
        var db = new Database { Name = "M", ID = "M", CompatibilityLevel = 1702, Model = new Model { Name = "Model" } };

        var sales = new Table { Name = "Sales" };
        var amount = new DataColumn { Name = "Amount", DataType = DataType.Double, SourceColumn = "Amount" };
        sales.Columns.Add(amount);
        sales.Partitions.Add(new Partition
        {
            Name = "Sales",
            Mode = ModeType.Import,
            Source = new MPartitionSource { Expression = "let Source = #table({}, {}) in Source" }
        });
        sales.Measures.Add(new Measure { Name = "Rev", Expression = "1" });
        var geo = new Hierarchy { Name = "Geo" };
        geo.Levels.Add(new Level { Name = "Lvl", Ordinal = 0, Column = amount });
        sales.Hierarchies.Add(geo);
        db.Model.Tables.Add(sales);

        var cg = new Table { Name = "CG" };
        cg.CalculationGroup = new CalculationGroup();
        cg.Columns.Add(new DataColumn { Name = "Name", DataType = DataType.String, SourceColumn = "Name" });
        db.Model.Tables.Add(cg);

        db.Model.Roles.Add(new ModelRole { Name = "Reader", ModelPermission = ModelPermission.Read });

        return db;
    }
}
