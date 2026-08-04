using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Core.Paths;
using Tomix.Core.Properties;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Read/write parity for shared M expressions and DAX functions (#107/#133): everything the
/// mutator can create must be visible to the read side. Each case walks the full lifecycle —
/// add → snapshot (what ls/get see) → set → snapshot → rm → snapshot — through the same
/// summarizer, selector, and catalog projection the commands use.
/// </summary>
public sealed class ExpressionFunctionReadWriteParityTests
{
    [Theory]
    [InlineData("Expression", ModelObjectKind.Expression, "Expressions/Environment",
        "\"dev\" meta [IsParameterQuery=true]")]
    [InlineData("Function", ModelObjectKind.Function, "Functions/AddOne", "(x) => x + 1")]
    public void AddLsGetSetRm_RoundTripsThroughTheSnapshot(
        string addType, ModelObjectKind kind, string path, string value)
    {
        var db = NewDatabase();
        var mutator = new TomModelMutator(db);
        var name = path.Split('/')[1];

        // add
        var added = mutator.AddObject(new ModelObjectAddRequest(path, addType, value, [], IfNotExists: false));
        Assert.True(added.Changed);

        // ls: the container keyword resolves to exactly the new object
        var container = path.Split('/')[0];
        var listed = ModelObjectSelector.Select(TomModelSummarizer.Snapshot(db, "m"), container, type: null);
        var obj = Assert.Single(listed);
        Assert.Equal(kind, obj.Kind);
        Assert.Equal(path, obj.Path);
        Assert.Equal(name, obj.Name);
        Assert.Equal(value, obj.Expression);

        // get: the catalog projection surfaces the expression under its JSON key
        var projected = ModelPropertyCatalog.Project(obj);
        Assert.Equal(value, projected["expression"]);
        Assert.Equal(name, projected["name"]);

        // set: a description write is visible on the next snapshot
        mutator.SetProperty(new ModelObjectSetRequest(
            path, [new ModelPropertyAssignment("description", "round-trip")], kind));
        var afterSet = Assert.Single(ModelObjectSelector.Select(TomModelSummarizer.Snapshot(db, "m"), container, type: null));
        Assert.Equal("round-trip", afterSet.Description);

        // rm: the object disappears from the snapshot
        var removed = mutator.RemoveObject(new ModelObjectRemoveRequest(path, kind, IfExists: false));
        Assert.True(removed.Changed);
        Assert.Empty(ModelObjectSelector.Select(TomModelSummarizer.Snapshot(db, "m"), container, type: null));
    }

    [Fact]
    public void RewriteExpressions_ReachesFunctionBodies()
    {
        // Rename fixup routes dependency-graph sites through RewriteExpressions, so a measure
        // rename that touches a UDF body must land on the function's expression.
        var db = NewDatabase();
        db.Model.Functions.Add(new Function { Name = "F", Expression = "(x) => [Old] * x" });
        var mutator = new TomModelMutator(db);

        mutator.RewriteExpressions([new ModelExpressionEdit(
            "Functions/F", ModelObjectKind.Function, "Expression", "(x) => [New] * x")]);

        Assert.Equal("(x) => [New] * x", db.Model.Functions["F"].Expression);
    }

    [Theory]
    [InlineData("Expressions", ModelObjectKind.Expression)]
    [InlineData("Functions", ModelObjectKind.Function)]
    [InlineData("DataSources", ModelObjectKind.DataSource)]
    public void ContainerKeywords_ResolveToKinds(string keyword, ModelObjectKind kind)
    {
        var segment = Assert.Single(ObjectPath.Parse(keyword));
        Assert.True(segment.TryGetKeyword(out var parsed));
        Assert.Equal(kind, parsed);
    }

    private static Database NewDatabase()
    {
        // 1702+ so the model can carry DAX user-defined functions.
        var db = new Database { Name = "M", CompatibilityLevel = 1702, Model = new Model { Name = "Model" } };
        var table = new Table { Name = "T" };
        table.Partitions.Add(new Partition
        {
            Name = "T",
            Source = new MPartitionSource { Expression = "let x = 1 in x" }
        });
        table.Columns.Add(new DataColumn { Name = "C", DataType = DataType.Int64 });
        db.Model.Tables.Add(table);
        return db;
    }
}
