using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Properties;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Drift guards between <see cref="ModelPropertyCatalog"/> and the mutator: every property the
/// catalog advertises as writable (surfaced in set/add error hints) must actually be accepted by
/// <see cref="TomModelMutator"/>, and every catalog search scope must be a valid replace scope.
/// If a setter is added or removed in the mutator, update the catalog's Writable flags with it.
/// </summary>
public sealed class CatalogWritableAgreementTests
{
    private static readonly IReadOnlyDictionary<string, string> ValidValues =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = "Renamed",
            ["description"] = "described",
            ["isHidden"] = "true",
            ["dataCategory"] = "Time",
            ["expression"] = "2",
            ["formatString"] = "#,0",
            ["displayFolder"] = "Folder"
        };

    public static TheoryData<ModelObjectKind> CatalogedKinds
        => new(ModelObjectKind.Table, ModelObjectKind.Measure, ModelObjectKind.Column,
            ModelObjectKind.Hierarchy, ModelObjectKind.Partition);

    [Theory]
    [MemberData(nameof(CatalogedKinds))]
    public void EveryCatalogWritableProperty_IsAcceptedByTheMutator(ModelObjectKind kind)
    {
        var tokens = ModelPropertyCatalog.WritableTokens(kind);
        Assert.NotEmpty(tokens);

        foreach (var token in tokens)
        {
            // Fresh model per property so a 'name' assignment cannot invalidate later paths.
            var db = NewDatabase();
            var mutator = new TomModelMutator(db);
            var (path, type) = TargetFor(kind);

            var exception = Record.Exception(() => mutator.SetProperty(new ModelObjectSetRequest(
                path, [new ModelPropertyAssignment(token, ValidValues[token])], type)));

            Assert.True(exception is null,
                $"Catalog marks '{token}' writable on {kind}, but the mutator rejected it: {exception?.Message}");
        }
    }

    [Fact]
    public void EveryCatalogSearchScope_IsAValidReplaceScope()
    {
        foreach (var scope in ModelPropertyCatalog.SearchScopes)
        {
            var mutator = new TomModelMutator(NewDatabase());

            // An unknown scope throws ArgumentException before any operation is built.
            var exception = Record.Exception(() => mutator.ReplaceText(new ModelReplaceRequest(
                Pattern: "nothing-matches-this",
                Replacement: "x",
                Scope: scope,
                Regex: false,
                CaseSensitive: false,
                Apply: false)));

            Assert.True(exception is null,
                $"Catalog search scope '{scope}' is not accepted by replace: {exception?.Message}");
        }
    }

    [Fact]
    public void UnsupportedPartitionPropertyHint_OmitsExpression_ForNonMSources()
    {
        // 'expression' is only settable on M-source partitions, so the hint must not
        // advertise it for calculated/entity/policy-range partitions.
        var db = NewDatabase();
        db.Model.Tables["T"].Partitions["T"].Source = new CalculatedPartitionSource { Expression = "T2" };
        var mutator = new TomModelMutator(db);

        var exception = Assert.Throws<NotSupportedException>(() => mutator.SetProperty(new ModelObjectSetRequest(
            "T/T", [new ModelPropertyAssignment("bogus", "x")], ModelObjectKind.Partition)));

        Assert.DoesNotContain("expression", exception.Message);
        Assert.Contains("name", exception.Message);
    }

    [Fact]
    public void UnsupportedPartitionPropertyHint_IncludesExpression_ForMSources()
    {
        var mutator = new TomModelMutator(NewDatabase());

        var exception = Assert.Throws<NotSupportedException>(() => mutator.SetProperty(new ModelObjectSetRequest(
            "T/T", [new ModelPropertyAssignment("bogus", "x")], ModelObjectKind.Partition)));

        Assert.Contains("expression", exception.Message);
    }

    private static (string Path, ModelObjectKind? Type) TargetFor(ModelObjectKind kind) => kind switch
    {
        ModelObjectKind.Table => ("tables/T", null),
        ModelObjectKind.Measure => ("T/M", ModelObjectKind.Measure),
        ModelObjectKind.Column => ("T/C", ModelObjectKind.Column),
        ModelObjectKind.Hierarchy => ("T/H", ModelObjectKind.Hierarchy),
        ModelObjectKind.Partition => ("T/T", ModelObjectKind.Partition),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static Database NewDatabase()
    {
        var db = new Database { Name = "M", Model = new Model { Name = "Model" } };
        var table = new Table { Name = "T" };
        table.Partitions.Add(new Partition
        {
            Name = "T",
            Source = new MPartitionSource { Expression = "let x = 1 in x" }
        });
        table.Columns.Add(new DataColumn { Name = "C", DataType = DataType.Int64 });
        table.Measures.Add(new Measure { Name = "M", Expression = "1" });
        var hierarchy = new Hierarchy { Name = "H" };
        hierarchy.Levels.Add(new Level { Name = "L", Column = table.Columns["C"] });
        table.Hierarchies.Add(hierarchy);
        db.Model.Tables.Add(table);
        return db;
    }
}
