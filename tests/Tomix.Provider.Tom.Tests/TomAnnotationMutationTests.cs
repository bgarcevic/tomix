using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Bpa;
using Tomix.Core.Models;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Verifies the annotation read/write round-trip used by the BPA ignore store: the mutator writes
/// model-level and object-level annotations, and the summarizer reads them back into the snapshot.
/// </summary>
public sealed class TomAnnotationMutationTests
{
    private const string Key = BpaIgnoreStore.Key;
    private const string LegacyKey = BpaIgnoreStore.LegacyKey;

    [Fact]
    public void SetProperty_WritesModelLevelAnnotation_ReadBackBySummarizer()
    {
        var db = NewDatabase();
        var mutator = new TomModelMutator(db);

        mutator.SetProperty(new ModelObjectSetRequest(
            ".",
            [new ModelPropertyAssignment($"Annotation:{Key}", "{\"RuleIDs\":[\"RULE_A\"]}")],
            Type: null));

        var snapshot = TomModelSummarizer.Snapshot(db, "M");
        Assert.Equal("{\"RuleIDs\":[\"RULE_A\"]}", snapshot.Properties![$"Annotation:{Key}"]);
    }

    [Fact]
    public void SetProperty_MigratesLegacyKey_WritesCorrectAndRemovesMisspelled()
    {
        var db = NewDatabase();
        var mutator = new TomModelMutator(db);

        // Pre-existing ignore stored under the historical misspelled key.
        mutator.SetProperty(new ModelObjectSetRequest(
            ".", [new ModelPropertyAssignment($"Annotation:{LegacyKey}", "{\"RuleIDs\":[\"RULE_A\"]}")], null));

        // Save the migrated list: write the correct key, drop the legacy one (empty value = remove).
        mutator.SetProperty(new ModelObjectSetRequest(
            ".",
            [
                new ModelPropertyAssignment($"Annotation:{Key}", "{\"RuleIDs\":[\"RULE_A\"]}"),
                new ModelPropertyAssignment($"Annotation:{LegacyKey}", "")
            ],
            null));

        var props = TomModelSummarizer.Snapshot(db, "M").Properties!;
        Assert.True(props.ContainsKey($"Annotation:{Key}"));
        Assert.False(props.ContainsKey($"Annotation:{LegacyKey}"));
    }

    [Fact]
    public void SetProperty_WritesObjectLevelAnnotation_OnTable()
    {
        var db = NewDatabase();
        db.Model.Tables.Add(new Table { Name = "Sales" });
        var mutator = new TomModelMutator(db);

        mutator.SetProperty(new ModelObjectSetRequest(
            "Sales", [new ModelPropertyAssignment($"Annotation:{Key}", "{\"RuleIDs\":[\"RULE_B\"]}")], null));

        var table = TomModelSummarizer.Snapshot(db, "M").Objects.Single(o => o.Name == "Sales");
        Assert.Equal("{\"RuleIDs\":[\"RULE_B\"]}", table.Property($"Annotation:{Key}"));
    }

    [Fact]
    public void SetProperty_EmptyValue_RemovesAnnotation()
    {
        var db = NewDatabase();
        var mutator = new TomModelMutator(db);

        mutator.SetProperty(new ModelObjectSetRequest(
            ".", [new ModelPropertyAssignment($"Annotation:{Key}", "{\"RuleIDs\":[\"RULE_A\"]}")], null));
        mutator.SetProperty(new ModelObjectSetRequest(
            ".", [new ModelPropertyAssignment($"Annotation:{Key}", "")], null));

        var props = TomModelSummarizer.Snapshot(db, "M").Properties!;
        Assert.False(props.ContainsKey($"Annotation:{Key}"));
    }

    [Fact]
    public void SetProperty_WritesAnnotation_OnRelationshipEndpointPath()
    {
        var db = NewDatabase();
        var sales = new Table { Name = "Sales" };
        sales.Columns.Add(new DataColumn { Name = "CustomerId", DataType = DataType.Int64 });
        var customer = new Table { Name = "Customer" };
        customer.Columns.Add(new DataColumn { Name = "Id", DataType = DataType.Int64 });
        db.Model.Tables.Add(sales);
        db.Model.Tables.Add(customer);
        db.Model.Relationships.Add(new SingleColumnRelationship
        {
            Name = "SalesToCustomer",
            FromColumn = sales.Columns["CustomerId"],
            ToColumn = customer.Columns["Id"]
        });
        var mutator = new TomModelMutator(db);

        // The vertipaq --annotate flow addresses relationships by their endpoint path.
        var result = mutator.SetProperty(new ModelObjectSetRequest(
            "'Sales'[CustomerId]->'Customer'[Id]",
            [new ModelPropertyAssignment("Annotation:Vertipaq_RIViolationInvalidRows", "3")],
            ModelObjectKind.Relationship));

        Assert.True(result.Changed);
        var annotation = db.Model.Relationships["SalesToCustomer"].Annotations["Vertipaq_RIViolationInvalidRows"];
        Assert.Equal("3", annotation.Value);
    }

    private static Database NewDatabase()
        => new() { Name = "M", Model = new Model { Name = "Model" } };
}
