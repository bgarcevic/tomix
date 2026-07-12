using Tomix.Core.Models;
using Tomix.Provider.Tom;
using Microsoft.AnalysisServices.Tabular;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// <c>tx replace --in</c> scope handling: unknown scopes hard-error instead of silently matching
/// nothing, and the advertised <c>annotations</c> scope actually replaces annotation values
/// (explicit-only — <c>all</c> must not touch annotations).
/// </summary>
public sealed class TomReplaceScopeTests
{
    [Fact]
    public void UnknownScope_Throws()
    {
        var mutator = new TomModelMutator(WithSales());

        var ex = Assert.Throws<ArgumentException>(() => mutator.ReplaceText(
            Replace("Sales", "Orders", scope: "descriptionz")));
        Assert.Contains("Unknown replace scope: 'descriptionz'", ex.Message);
        Assert.Contains("annotations", ex.Message);
    }

    [Fact]
    public void AnnotationsScope_ReplacesAnnotationValues()
    {
        var db = WithSales();
        var sales = db.Model.Tables.Single(t => t.Name == "Sales");
        sales.Annotations.Add(new Annotation { Name = "Tag", Value = "old-value" });
        db.Model.Annotations.Add(new Annotation { Name = "ModelTag", Value = "old-value" });
        var mutator = new TomModelMutator(db);

        var result = mutator.ReplaceText(Replace("old-value", "new-value", scope: "annotations"));

        Assert.Equal(2, result.ChangeCount);
        Assert.Equal("new-value", sales.Annotations["Tag"].Value);
        Assert.Equal("new-value", db.Model.Annotations["ModelTag"].Value);
        Assert.Contains(result.Previews, p => p.Property == "Annotation:Tag");
    }

    [Fact]
    public void AnnotationsScope_DryRun_LeavesValuesUntouched()
    {
        var db = WithSales();
        var sales = db.Model.Tables.Single(t => t.Name == "Sales");
        sales.Annotations.Add(new Annotation { Name = "Tag", Value = "old-value" });
        var mutator = new TomModelMutator(db);

        var result = mutator.ReplaceText(Replace("old-value", "new-value", scope: "annotations", apply: false));

        Assert.Equal(1, result.ChangeCount);
        Assert.Equal("old-value", sales.Annotations["Tag"].Value);
    }

    [Fact]
    public void AllScope_DoesNotTouchAnnotations()
    {
        var db = WithSales();
        var sales = db.Model.Tables.Single(t => t.Name == "Sales");
        sales.Annotations.Add(new Annotation { Name = "Tag", Value = "old-value" });
        var mutator = new TomModelMutator(db);

        var result = mutator.ReplaceText(Replace("old-value", "new-value", scope: "all"));

        Assert.Equal("old-value", sales.Annotations["Tag"].Value);
        Assert.DoesNotContain(result.Previews, p => p.Property.StartsWith("Annotation:"));
    }

    private static ModelReplaceRequest Replace(string pattern, string replacement, string scope, bool apply = true)
        => new(pattern, replacement, scope, Regex: false, CaseSensitive: false, Apply: apply);

    private static Database WithSales()
    {
        var db = new Database { Name = "M", Model = new Model { Name = "Model" } };
        var sales = new Table { Name = "Sales" };
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
