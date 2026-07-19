using Tomix.App.Models;
using Tomix.Core.Authentication;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Tests;

public sealed class ModelSessionRunnerTests
{
    private static readonly ModelReference RemoteModel =
        ModelReference.Remote("powerbi://api.powerbi.com/v1.0/myorg/Workspace", "Sales");

    [Fact]
    public async Task RunAsync_NoProvider_ReturnsStandardDiagnostic()
    {
        var result = await ModelSessionRunner.RunAsync<string>(
            [], new ModelReference("model.unknown"),
            _ => Task.FromResult(TomixResult<string>.Ok("unused")),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_NO_PROVIDER", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_OpensRunsAndDisposesSession()
    {
        var session = new RecordingSession();

        var result = await ModelSessionRunner.RunAsync(
            new IModelProvider[] { new StubProvider(session) },
            new ModelReference("model.bim"),
            opened => Task.FromResult(TomixResult<string>.Ok(opened.SourcePath)),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("model.bim", result.Data);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task RunAsync_DisposesSessionWhenActionThrows()
    {
        var session = new RecordingSession();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ModelSessionRunner.RunAsync<string>(
            new IModelProvider[] { new StubProvider(session) },
            new ModelReference("model.bim"),
            _ => Task.FromException<TomixResult<string>>(new InvalidOperationException("boom")),
            CancellationToken.None));

        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task RunAsync_RemoteAuthenticationFailure_UsesConnectionContract()
    {
        var result = await ModelSessionRunner.RunAsync<string>(
            new IModelProvider[] { new ThrowingProvider(new AuthenticationRequiredException("login")) },
            RemoteModel,
            _ => Task.FromResult(TomixResult<string>.Ok("unused")),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_AUTH_REQUIRED", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task RunAsync_CancellationPropagates()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() => ModelSessionRunner.RunAsync<string>(
            new IModelProvider[] { new ThrowingProvider(new OperationCanceledException()) },
            RemoteModel,
            _ => Task.FromResult(TomixResult<string>.Ok("unused")),
            CancellationToken.None));
    }

    private sealed class StubProvider(IModelSession session) : IModelProvider
    {
        public bool CanOpen(ModelReference reference) => true;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken cancellationToken)
            => Task.FromResult(session);
    }

    private sealed class ThrowingProvider(Exception error) : IModelProvider
    {
        public bool CanOpen(ModelReference reference) => true;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken cancellationToken)
            => Task.FromException<IModelSession>(error);
    }

    private sealed class RecordingSession : IModelSession
    {
        public string SourcePath => "model.bim";
        public bool Disposed { get; private set; }

        public Task<ModelSummary> GetSummaryAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
