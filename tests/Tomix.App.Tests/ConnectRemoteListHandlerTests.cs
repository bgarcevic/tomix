using Tomix.App.Connect;
using Tomix.Core.Authentication;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class ConnectRemoteListHandlerTests
{
    [Fact]
    public async Task BareWorkspaceName_IsNormalized_AndModelsAreSortedByName()
    {
        var catalog = new RecordingCatalog(new ServerDatabaseInfo("Sales", 1604), new ServerDatabaseInfo("finance"));

        var result = await new ConnectRemoteListHandler([catalog]).HandleAsync("My Workspace", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/My Workspace", result.Data!.Server);
        Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/My Workspace", catalog.Listed!.Value);
        Assert.Null(catalog.Listed.Database);
        Assert.Equal(["finance", "Sales"], result.Data.Models.Select(m => m.Name));
        Assert.Equal(1604, result.Data.Models[1].CompatibilityLevel);
    }

    [Fact]
    public async Task EmptyEndpoint_IsAnEmptySuccess()
    {
        var result = await new ConnectRemoteListHandler([new RecordingCatalog()])
            .HandleAsync("powerbi://api.powerbi.com/v1.0/myorg/Empty", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Models);
    }

    [Fact]
    public async Task NoCatalog_IsNoProvider()
    {
        var result = await new ConnectRemoteListHandler([]).HandleAsync("Workspace", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_NO_PROVIDER", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Theory]
    [InlineData(typeof(AuthenticationRequiredException), "TOMIX_AUTH_REQUIRED")]
    [InlineData(typeof(InvalidOperationException), "TOMIX_REMOTE_LIST_FAILED")]
    public async Task ListingFailure_MapsToDiagnostic(Type exceptionType, string code)
    {
        var failure = (Exception)Activator.CreateInstance(exceptionType, "boom")!;
        var result = await new ConnectRemoteListHandler([new RecordingCatalog { Failure = failure }])
            .HandleAsync("Workspace", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(code, result.Diagnostics[0].Code);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("boom", result.Diagnostics[0].Message);
    }

    private sealed class RecordingCatalog(params ServerDatabaseInfo[] databases) : IModelProvider, IServerCatalog
    {
        public Exception? Failure { get; init; }

        public ModelReference? Listed { get; private set; }

        public bool CanOpen(ModelReference reference) => false;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public bool CanList(ModelReference endpoint) => endpoint.IsRemote;

        public Task<IReadOnlyList<ServerDatabaseInfo>> ListDatabasesAsync(ModelReference endpoint, CancellationToken cancellationToken)
        {
            Listed = endpoint;
            return Failure is null
                ? Task.FromResult<IReadOnlyList<ServerDatabaseInfo>>(databases)
                : Task.FromException<IReadOnlyList<ServerDatabaseInfo>>(Failure);
        }
    }
}
