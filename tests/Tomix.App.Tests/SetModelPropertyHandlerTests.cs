using Tomix.App.Set;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class SetModelPropertyHandlerTests
{
    [Fact]
    public async Task HandleAsync_Fails_WhenNoPropertyGiven()
    {
        var handler = new SetModelPropertyHandler([]);
        var result = await handler.HandleAsync(
            NewRequest(properties: [], revert: false),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_SET_PROPERTY_REQUIRED", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_Fails_WhenRevertCombinedWithAssignment()
    {
        // A -q/-i next to --revert must hard-error instead of silently dropping the assignment.
        var handler = new SetModelPropertyHandler([]);
        var result = await handler.HandleAsync(
            NewRequest(properties: [new ModelPropertyAssignment("description", "x")], revert: true),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_STAGE_OPTIONS_CONFLICT", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    private static SetModelPropertyRequest NewRequest(
        IReadOnlyList<ModelPropertyAssignment> properties,
        bool revert)
        => new(
            new ModelReference("model.bim"),
            "Sales",
            properties,
            Type: null,
            Save: false,
            SaveTo: null,
            Serialization: "",
            Force: false,
            Stage: false,
            Revert: revert,
            NoSync: false);
}
