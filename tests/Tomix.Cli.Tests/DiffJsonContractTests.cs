using System.Text.Json;
using Tomix.App.Diff;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>
/// Pins the existing diff JSON contract through the diff handler and production serializer.
/// Modified rows deliberately use objectType for kind/path and path for the property name.
/// </summary>
public sealed class DiffJsonContractTests
{
    [Fact]
    public void Json_PreservesAddedRemovedAndModifiedRowShapes()
    {
        var left = Snapshot(
            Measure("Removed"),
            Measure("Updated", "before"),
            Measure("Gained"),
            Measure("Lost", "before"));
        var right = Snapshot(
            Measure("Added"),
            Measure("Updated", "after"),
            Measure("Gained", "after"),
            Measure("Lost"));
        var result = DiffModelHandler.Diff(left, right, ignoreEngineComputedState: false);

        using var json = JsonDocument.Parse(JsonOutput.Serialize(
            new CommandEnvelope<DiffModelResult>(result, [])));
        var root = json.RootElement;
        Assert.Equal(["data", "diagnostics"], Keys(root));
        Assert.Empty(root.GetProperty("diagnostics").EnumerateArray());

        var data = root.GetProperty("data");
        Assert.True(data.GetProperty("hasChanges").GetBoolean());
        var summary = data.GetProperty("summary");
        Assert.Equal(1, summary.GetProperty("added").GetInt32());
        Assert.Equal(1, summary.GetProperty("removed").GetInt32());
        Assert.Equal(3, summary.GetProperty("modified").GetInt32());

        var changes = data.GetProperty("changes").EnumerateArray().ToArray();
        Assert.Equal(5, changes.Length);

        var added = Assert.Single(changes, change => Action(change) == "added");
        Assert.Equal(["action", "objectType", "path"], Keys(added));
        Assert.Equal("Measure", added.GetProperty("objectType").GetString());
        Assert.Equal("Sales/Added", added.GetProperty("path").GetString());

        var removed = Assert.Single(changes, change => Action(change) == "removed");
        Assert.Equal(["action", "objectType", "path"], Keys(removed));
        Assert.Equal("Measure", removed.GetProperty("objectType").GetString());
        Assert.Equal("Sales/Removed", removed.GetProperty("path").GetString());

        var updated = Assert.Single(changes, change =>
            Action(change) == "modified" && ObjectType(change) == "Measure/Sales/Updated");
        Assert.Equal(["action", "newValue", "objectType", "oldValue", "path"], Keys(updated));
        Assert.Equal("Description", updated.GetProperty("path").GetString());
        Assert.Equal("before", updated.GetProperty("oldValue").GetString());
        Assert.Equal("after", updated.GetProperty("newValue").GetString());

        var gained = Assert.Single(changes, change =>
            Action(change) == "modified" && ObjectType(change) == "Measure/Sales/Gained");
        Assert.Equal(["action", "newValue", "objectType", "path"], Keys(gained));
        Assert.Equal("Description", gained.GetProperty("path").GetString());
        Assert.Equal("after", gained.GetProperty("newValue").GetString());

        var lost = Assert.Single(changes, change =>
            Action(change) == "modified" && ObjectType(change) == "Measure/Sales/Lost");
        Assert.Equal(["action", "objectType", "oldValue", "path"], Keys(lost));
        Assert.Equal("Description", lost.GetProperty("path").GetString());
        Assert.Equal("before", lost.GetProperty("oldValue").GetString());
    }

    private static string Action(JsonElement change) => change.GetProperty("action").GetString()!;

    private static string ObjectType(JsonElement change) => change.GetProperty("objectType").GetString()!;

    private static string[] Keys(JsonElement element)
        => element.EnumerateObject().Select(property => property.Name).Order().ToArray();

    private static ModelSnapshot Snapshot(params ModelObject[] measures)
        => new("stub", 1601,
        [
            new ModelObject("Sales", ModelObjectKind.Table, "Sales", "regular", null, null,
                false, null, measures)
        ]);

    private static ModelObject Measure(string name, string? description = null)
        => new(name, ModelObjectKind.Measure, $"Sales/{name}", null, "1", description,
            false, null, []);
}
