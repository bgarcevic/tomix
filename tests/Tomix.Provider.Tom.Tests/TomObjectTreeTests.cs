using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// <see cref="TomObjectTree"/> must describe exactly the objects <see cref="TomModelSummarizer"/>
/// snapshots, with the same kinds and paths: change events and snapshot lookups meet on them.
/// </summary>
public sealed class TomObjectTreeTests
{
    public static TheoryData<string> SampleModels => new(
        Directory.GetDirectories(RepoPaths.Samples, "*.SemanticModel")
            .Where(dir => Directory.Exists(Path.Combine(dir, "definition")))
            .Select(dir => Path.GetFileName(dir)));

    [Theory]
    [MemberData(nameof(SampleModels))]
    public void Walk_MatchesTheSnapshotsObjectsKindsAndPaths(string sample)
    {
        var database = TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(RepoPaths.Samples, sample, "definition"));
        AssertWalkMatchesSnapshot(database);
    }

    [Fact]
    public void Walk_MatchesTheSnapshot_ForEveryKindTheMutatorsTouch()
        => AssertWalkMatchesSnapshot(JournalFixture.Rich());

    [Fact]
    public void OwnerOf_AttributesUntrackedChildrenToTheObjectTheyDescribe()
    {
        var database = JournalFixture.Rich();
        var model = database.Model;
        var revenue = model.Tables["Sales"].Measures["Revenue"];
        var translation = model.Cultures["da-DK"].ObjectTranslations[revenue, TranslatedProperty.Caption];
        var partition = model.Tables["Sales"].Partitions["Sales 2023"];

        Assert.Same(revenue, TomObjectTree.OwnerOf(revenue.Annotations["Owner"]));
        Assert.Same(revenue, TomObjectTree.OwnerOf(translation));
        Assert.Same(partition, TomObjectTree.OwnerOf(partition.Source));
        Assert.Same(model.Perspectives["Reporting"], TomObjectTree.OwnerOf(model.Perspectives["Reporting"].PerspectiveTables["Sales"]));
        Assert.Same(model, TomObjectTree.OwnerOf(database));
    }

    private static void AssertWalkMatchesSnapshot(Database database)
    {
        var ids = new TomObjectIdMap();
        var snapshot = TomModelSummarizer.Snapshot(database, "M", ids);

        var fromSnapshot = new List<(ObjectId, ModelObjectKind, string)>();
        var pending = new Stack<ModelObject>(snapshot.Objects);
        while (pending.TryPop(out var node))
        {
            fromSnapshot.Add((node.Id!.Value, node.Kind, node.Path));
            foreach (var child in node.Children)
                pending.Push(child);
        }

        var fromWalk = TomObjectTree.Walk(database.Model)
            .Where(o => o is not Model)
            .Select(o => (ids.GetOrAdd(o), TomObjectTree.KindOf(o)!.Value, TomObjectTree.PathOf(o)))
            .ToList();

        Assert.NotEmpty(fromWalk);
        Assert.Equal(fromSnapshot.Order(), fromWalk.Order());
    }
}
