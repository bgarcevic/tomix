using Mdl.App.Info;
using Mdl.Core.Models;

namespace Mdl.App.Tests;

public sealed class InfoModelHandlerTests
{
    [Fact]
    public async Task HandleAsync_ReturnsSuccess_WhenProviderCanOpen()
    {
        var handler = new InfoModelHandler([new StubModelProvider()]);
        var result  = await handler.HandleAsync(
            new InfoModelRequest(new ModelReference("any")),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("stub", result.Data!.Summary.Name);
    }

    [Fact]
    public async Task HandleAsync_ReturnsFail_WhenNoProviderMatches()
    {
        var handler = new InfoModelHandler([]);
        var result  = await handler.HandleAsync(
            new InfoModelRequest(new ModelReference("any")),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("MDL_NO_PROVIDER", result.Diagnostics[0].Code);
    }

    private sealed class StubModelProvider : IModelProvider
    {
        public bool CanOpen(ModelReference _) => true;

        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken ct)
            => Task.FromResult<IModelSession>(new StubSession());
    }

    private sealed class StubSession : IModelSession
    {
        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 3, 12, 4, 2, 0));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
