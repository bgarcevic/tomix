using System.Text.Json.Nodes;
using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Provider.Tom;
using CompatibilityMode = Microsoft.AnalysisServices.CompatibilityMode;
using TabularJsonSerializer = Microsoft.AnalysisServices.Tabular.JsonSerializer;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Model files are git artifacts: CI on Linux and contributors on Windows must not fight over
/// line endings. These tests pin that the .bim writer emits LF-only, BOM-less, byte-stable
/// output on every OS, and that collection order follows TOM model order (#256).
/// </summary>
public sealed class TomExporterBimTests
{
    [Fact]
    public async Task ExportBim_EmitsLfOnly_AndNoBom()
    {
        using var dir = new TempDir();
        var bimPath = await ExportBimAsync(dir);

        var bytes = await File.ReadAllBytesAsync(bimPath);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.DoesNotContain((byte)'\r', bytes);
    }

    [Fact]
    public async Task ExportBim_IsByteDeterministic()
    {
        using var dir = new TempDir();
        var first = await File.ReadAllBytesAsync(await ExportBimAsync(dir));
        var second = await File.ReadAllBytesAsync(await ExportBimAsync(dir));

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ExportBim_RoundTripsThroughTom()
    {
        using var dir = new TempDir();
        var bimPath = await ExportBimAsync(dir);

        var fromBim = TabularJsonSerializer.DeserializeDatabase(
            File.ReadAllText(bimPath), new DeserializeOptions(), CompatibilityMode.PowerBI);

        Assert.Equal(["Sales", "Customer"], fromBim.Model.Tables.Select(t => t.Name).ToList());
        Assert.Equal(
            ["CustomerId", "Amount", "MonthName", "MonthNo"],
            fromBim.Model.Tables["Sales"].Columns.Select(c => c.Name).ToList());
        var relationship = Assert.Single(fromBim.Model.Relationships);
        Assert.Equal("SalesToCustomer", relationship.Name);
    }

    /// <summary>
    /// The exporter must not sort: property and array order comes straight from TOM model order
    /// (NamedMetadataObjectCollection insertion order), and the null-removal pass preserves it.
    /// Reordering would churn every user's git diff on upgrade.
    /// </summary>
    [Fact]
    public async Task ExportBim_PreservesTomModelOrder_NotAlphabetical()
    {
        var db = TestModels.NewDatabase(compatibilityLevel: 1702);
        db.ID = "M";
        var zebra = TestModels.NewTable("Zebra", "Qty", "Amount", "Name");
        zebra.Measures.Add(new Measure { Name = "Total Qty", Expression = "SUM(Zebra[Qty])" });
        zebra.Measures.Add(new Measure { Name = "Average Qty", Expression = "AVERAGE(Zebra[Qty])" });
        db.Model.Tables.Add(zebra);
        db.Model.Tables.Add(TestModels.NewTable("Alpha", "Id"));
        db.Model.Tables.Add(TestModels.NewTable("Mango", "Id"));

        using var dir = new TempDir();
        var bimPath = await ExportBimAsync(dir, db);

        var model = JsonNode.Parse(File.ReadAllText(bimPath))!["model"]!.AsObject();
        Assert.Equal(["Zebra", "Alpha", "Mango"], Names(model["tables"]));
        Assert.Equal(["Qty", "Amount", "Name"], Names(model["tables"]![0]!["columns"]));
        Assert.Equal(["Total Qty", "Average Qty"], Names(model["tables"]![0]!["measures"]));
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static async Task<string> ExportBimAsync(TempDir dir, Database? database = null)
    {
        var db = database ?? NewDatabase();
        var path = dir.Combine("model.bim");
        await TomModelExporter.ExportAsync(
            db, new ModelExportRequest(path, "bim", Overwrite: true, SupportingFiles: false), CancellationToken.None);
        return path;
    }

    private static Database NewDatabase()
    {
        var db = TestModels.WithRelationship();
        db.ID = "M";
        db.CompatibilityLevel = 1702;
        return db;
    }

    private static List<string> Names(JsonNode? array)
        => array!.AsArray().Select(n => n!["name"]!.GetValue<string>()).ToList();
}
