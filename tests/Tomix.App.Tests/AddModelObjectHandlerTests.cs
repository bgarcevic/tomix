using Tomix.App.Add;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class AddModelObjectHandlerTests
{
    [Fact]
    public async Task HandleAsync_NoProvider_ReturnsNoProviderError()
    {
        var handler = new AddModelObjectHandler([]);

        var result = await handler.HandleAsync(
            new AddModelObjectRequest(
                new ModelReference("nonexistent.tmdl"),
                "Sales/Revenue",
                "Measure",
                "SUM(Sales[Amount])",
                [],
                IfNotExists: false,
                Save: false,
                SaveTo: null,
                Serialization: "",
                Force: false),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("TOMIX_NO_PROVIDER", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task HandleAsync_NonMutationSession_ReturnsUnsupportedProviderError()
    {
        var handler = new AddModelObjectHandler([new ReadOnlyProvider()]);

        var result = await handler.HandleAsync(
            new AddModelObjectRequest(
                new ModelReference("any"),
                "Sales/Revenue",
                "Measure",
                "SUM(Sales[Amount])",
                [],
                IfNotExists: false,
                Save: false,
                SaveTo: null,
                Serialization: "",
                Force: false),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_MUTATION_UNSUPPORTED_PROVIDER", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task HandleAsync_DryRun_ReturnsAddedPath()
    {
        var session = new StubMutationSession();
        var handler = new AddModelObjectHandler([new StubProvider(session)]);

        var result = await handler.HandleAsync(
            new AddModelObjectRequest(
                new ModelReference("any"),
                "Sales/Revenue",
                "Measure",
                "SUM(Sales[Amount])",
                [],
                IfNotExists: false,
                Save: false,
                SaveTo: null,
                Serialization: "",
                Force: false),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Sales/Revenue", result.Data!.Added);
        Assert.Equal(false, result.Data.Saved);
        Assert.Null(result.Data.Staged);
    }

    [Fact]
    public async Task HandleAsync_WithSave_ReturnsSavedPath()
    {
        var session = new StubMutationSession();
        var handler = new AddModelObjectHandler([new StubProvider(session)]);

        var result = await handler.HandleAsync(
            new AddModelObjectRequest(
                new ModelReference("any"),
                "Sales/Revenue",
                "Measure",
                "SUM(Sales[Amount])",
                [],
                IfNotExists: false,
                Save: true,
                SaveTo: null,
                Serialization: "",
                Force: false),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Sales/Revenue", result.Data!.Added);
        Assert.Equal("source", result.Data.Saved);
        Assert.Null(result.Data.Staged);
    }

    [Fact]
    public async Task HandleAsync_WithSaveTo_ReturnsSaveToPath()
    {
        var session = new StubMutationSession();
        var handler = new AddModelObjectHandler([new StubProvider(session)]);

        var result = await handler.HandleAsync(
            new AddModelObjectRequest(
                new ModelReference("any"),
                "Sales/Revenue",
                "Measure",
                "SUM(Sales[Amount])",
                [],
                IfNotExists: false,
                Save: false,
                SaveTo: "output/path",
                Serialization: "bim",
                Force: false),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("output/path", result.Data!.Saved);
        Assert.Equal("output/path", session.SaveOutputPath);
    }

    [Fact]
    public async Task HandleAsync_WithProperties_PassesPropertiesToProvider()
    {
        var session = new StubMutationSession();
        var handler = new AddModelObjectHandler([new StubProvider(session)]);

        var properties = new List<ModelPropertyAssignment>
        {
            new("description", "My measure"),
            new("formatString", "$#,0")
        };

        var result = await handler.HandleAsync(
            new AddModelObjectRequest(
                new ModelReference("any"),
                "Sales/Revenue",
                "Measure",
                "SUM(Sales[Amount])",
                properties,
                IfNotExists: false,
                Save: false,
                SaveTo: null,
                Serialization: "",
                Force: false),
            CancellationToken.None);

        Assert.True(result.Success);
        var addRequest = Assert.Single(session.AddRequests);
        Assert.Equal(2, addRequest.Properties.Count);
        Assert.Equal("description", addRequest.Properties[0].Property);
        Assert.Equal("My measure", addRequest.Properties[0].Value);
        Assert.Equal("formatString", addRequest.Properties[1].Property);
        Assert.Equal("$#,0", addRequest.Properties[1].Value);
    }

    [Fact]
    public async Task HandleAsync_IfNotExists_PassesFlagToProvider()
    {
        var session = new StubMutationSession();
        var handler = new AddModelObjectHandler([new StubProvider(session)]);

        var result = await handler.HandleAsync(
            new AddModelObjectRequest(
                new ModelReference("any"),
                "Sales/Revenue",
                "Measure",
                "1",
                [],
                IfNotExists: true,
                Save: false,
                SaveTo: null,
                Serialization: "",
                Force: false),
            CancellationToken.None);

        Assert.True(result.Success);
        var addRequest = Assert.Single(session.AddRequests);
        Assert.True(addRequest.IfNotExists);
    }

    // --stage materializes a working copy on disk; that end-to-end behavior is covered by the
    // process-isolated StageCommandTests (which set TOMIX_CONFIG_DIR), not here.

    [Fact]
    public async Task HandleAsync_RevertWithNothingStaged_Fails()
    {
        var session = new StubMutationSession();
        var handler = new AddModelObjectHandler([new StubProvider(session)]);

        var result = await handler.HandleAsync(
            new AddModelObjectRequest(
                new ModelReference($"/nonexistent/{Guid.NewGuid():N}.bim"),
                "Sales/Revenue",
                "Measure",
                "1",
                [],
                IfNotExists: false,
                Save: false,
                SaveTo: null,
                Serialization: "",
                Force: false,
                Stage: false,
                Revert: true),
            CancellationToken.None);

        // Revert used to report success unconditionally — even with nothing staged (live QA finding).
        Assert.False(result.Success);
        Assert.Equal("TOMIX_STAGE_NOTHING_STAGED", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task HandleAsync_RevertWithSaveTo_ReturnsConflictError()
    {
        var session = new StubMutationSession();
        var handler = new AddModelObjectHandler([new StubProvider(session)]);

        var result = await handler.HandleAsync(
            new AddModelObjectRequest(
                new ModelReference("any"),
                "Sales/Revenue",
                "Measure",
                "1",
                [],
                IfNotExists: false,
                Save: false,
                SaveTo: "output/path",
                Serialization: "",
                Force: false,
                Stage: false,
                Revert: true),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_STAGE_OPTIONS_CONFLICT", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--save-to", result.Diagnostics[0].Message);
        Assert.Empty(session.AddRequests);
    }

    [Fact]
    public async Task HandleAsync_IfNotExistsNoOp_ReturnsExistingPath()
    {
        var session = new StubMutationSession { ReturnChanged = false };
        var handler = new AddModelObjectHandler([new StubProvider(session)]);

        var result = await handler.HandleAsync(
            new AddModelObjectRequest(
                new ModelReference("any"),
                "Sales/Revenue",
                "Measure",
                "1",
                [],
                IfNotExists: true,
                Save: false,
                SaveTo: null,
                Serialization: "",
                Force: false),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(false, result.Data!.Added);
        Assert.Equal("Sales/Revenue", result.Data.ExistingPath);
        Assert.False(result.Data.Reverted);
    }

    [Fact]
    public async Task HandleAsync_UnsupportedAddOption_ReturnsOptionUnsupportedError()
    {
        var session = new ThrowingMutationSession(
            new UnsupportedAddOptionException("--columns is not supported for type 'CalcGroup'. It applies to: Table."));
        var handler = new AddModelObjectHandler([new StubProvider(session)]);

        var result = await handler.HandleAsync(
            new AddModelObjectRequest(
                new ModelReference("any"),
                "CG",
                "CalcGroup",
                null,
                [],
                IfNotExists: false,
                Save: false,
                SaveTo: null,
                Serialization: "",
                Force: false,
                Columns: "X,Y"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_ADD_OPTION_UNSUPPORTED", result.Diagnostics[0].Code);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_NewSourceAndRangeFields_PassThroughToProvider()
    {
        var session = new StubMutationSession();
        var handler = new AddModelObjectHandler([new StubProvider(session)]);

        var result = await handler.HandleAsync(
            new AddModelObjectRequest(
                new ModelReference("any"),
                "Sales/PR",
                "PolicyRangePartition",
                null,
                [],
                IfNotExists: false,
                Save: false,
                SaveTo: null,
                Serialization: "",
                Force: false,
                SourceSchema: "dbo",
                RangeStart: "2024-01-01",
                RangeEnd: "2025-01-01",
                RangeGranularity: "Month"),
            CancellationToken.None);

        Assert.True(result.Success);
        var addRequest = Assert.Single(session.AddRequests);
        Assert.Equal("dbo", addRequest.SourceSchema);
        Assert.Equal("2024-01-01", addRequest.RangeStart);
        Assert.Equal("2025-01-01", addRequest.RangeEnd);
        Assert.Equal("Month", addRequest.RangeGranularity);
    }

    [Fact]
    public async Task HandleAsync_UnsupportedMutation_ReturnsUnsupportedError()
    {
        var session = new ThrowingMutationSession(new NotSupportedException("bad op"));
        var handler = new AddModelObjectHandler([new StubProvider(session)]);

        var result = await handler.HandleAsync(
            new AddModelObjectRequest(
                new ModelReference("any"),
                "Sales/Revenue",
                "Measure",
                "1",
                [],
                IfNotExists: false,
                Save: false,
                SaveTo: null,
                Serialization: "",
                Force: false),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_MUTATION_UNSUPPORTED", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task HandleAsync_InvalidArgument_ReturnsInvalidValueError()
    {
        var session = new ThrowingMutationSession(new ArgumentException("bad arg"));
        var handler = new AddModelObjectHandler([new StubProvider(session)]);

        var result = await handler.HandleAsync(
            new AddModelObjectRequest(
                new ModelReference("any"),
                "Sales/Revenue",
                "Measure",
                "1",
                [],
                IfNotExists: false,
                Save: false,
                SaveTo: null,
                Serialization: "",
                Force: false),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_MUTATION_INVALID_VALUE", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task HandleAsync_InvalidOperation_ReturnsFailedError()
    {
        var session = new ThrowingMutationSession(new InvalidOperationException("fail"));
        var handler = new AddModelObjectHandler([new StubProvider(session)]);

        var result = await handler.HandleAsync(
            new AddModelObjectRequest(
                new ModelReference("any"),
                "Sales/Revenue",
                "Measure",
                "1",
                [],
                IfNotExists: false,
                Save: false,
                SaveTo: null,
                Serialization: "",
                Force: false),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_MUTATION_FAILED", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task HandleAsync_SaveIOException_ReturnsSaveFailedError()
    {
        var session = new SaveThrowingMutationSession();
        var handler = new AddModelObjectHandler([new StubProvider(session)]);

        var result = await handler.HandleAsync(
            new AddModelObjectRequest(
                new ModelReference("any"),
                "Sales/Revenue",
                "Measure",
                "1",
                [],
                IfNotExists: false,
                Save: true,
                SaveTo: null,
                Serialization: "",
                Force: false),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_MUTATION_SAVE_FAILED", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_ForceFlag_PassesForceToSave()
    {
        var session = new StubMutationSession();
        var handler = new AddModelObjectHandler([new StubProvider(session)]);

        var result = await handler.HandleAsync(
            new AddModelObjectRequest(
                new ModelReference("any"),
                "Sales/Revenue",
                "Measure",
                "1",
                [],
                IfNotExists: false,
                Save: true,
                SaveTo: null,
                Serialization: "",
                Force: true),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(session.SaveForceValue);
    }

    [Fact]
    public async Task HandleAsync_SerializationFormat_PassesToSave()
    {
        var session = new StubMutationSession();
        var handler = new AddModelObjectHandler([new StubProvider(session)]);

        var result = await handler.HandleAsync(
            new AddModelObjectRequest(
                new ModelReference("any"),
                "Sales/Revenue",
                "Measure",
                "1",
                [],
                IfNotExists: false,
                Save: true,
                SaveTo: null,
                Serialization: "bim",
                Force: false),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("bim", session.SaveSerializationValue);
    }

    private sealed class ReadOnlyProvider : IModelProvider
    {
        public bool CanOpen(ModelReference reference) => true;
        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken ct)
            => Task.FromResult<IModelSession>(new ReadOnlySession());
    }

    private sealed class ReadOnlySession : IModelSession
    {
        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken ct)
            => Task.FromResult(new ModelSummary("ro", 1601, 0, 0, 0, 0, 0));
        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken ct)
            => Task.FromResult(new ModelSnapshot("ro", 1601, []));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubProvider : IModelProvider
    {
        private readonly IModelSession _session;

        public StubProvider(IModelSession session) => _session = session;

        public bool CanOpen(ModelReference reference) => true;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken ct)
            => Task.FromResult(_session);
    }

    private sealed class StubMutationSession : IModelSession, IModelMutationSession
    {
        public string SourcePath => "";

        public List<ModelObjectAddRequest> AddRequests { get; } = [];
        public bool ReturnChanged { get; set; } = true;
        public string? SaveOutputPath { get; private set; }
        public string SaveSerializationValue { get; private set; } = "";
        public bool SaveForceValue { get; private set; }

        public Task<ModelSummary> GetSummaryAsync(CancellationToken ct)
            => Task.FromResult(new ModelSummary("stub", 1601, 0, 0, 0, 0, 0));
        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken ct)
            => Task.FromResult(new ModelSnapshot("stub", 1601, []));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ModelObjectMutationResult AddObject(ModelObjectAddRequest request)
        {
            AddRequests.Add(request);
            return new ModelObjectMutationResult(request.Path, Changed: ReturnChanged);
        }

        public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request)
            => throw new NotSupportedException();
        public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request)
            => throw new NotSupportedException();
        public ModelReplaceResult ReplaceText(ModelReplaceRequest request)
            => throw new NotSupportedException();

        public Task<ModelExportResult> SaveAsync(
            string? outputPath, string serialization, bool force, CancellationToken ct)
        {
            SaveOutputPath = outputPath;
            SaveSerializationValue = serialization;
            SaveForceValue = force;
            return Task.FromResult(new ModelExportResult(outputPath ?? "source", serialization));
        }
    }

    private sealed class ThrowingMutationSession : IModelSession, IModelMutationSession
    {
        private readonly Exception _exception;

        public ThrowingMutationSession(Exception exception) => _exception = exception;

        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken ct)
            => Task.FromResult(new ModelSummary("throw", 1601, 0, 0, 0, 0, 0));
        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken ct)
            => Task.FromResult(new ModelSnapshot("throw", 1601, []));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ModelObjectMutationResult AddObject(ModelObjectAddRequest request) => throw _exception;
        public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request) => throw new NotSupportedException();
        public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request) => throw new NotSupportedException();
        public ModelReplaceResult ReplaceText(ModelReplaceRequest request) => throw new NotSupportedException();
        public Task<ModelExportResult> SaveAsync(string? outputPath, string serialization, bool force, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class SaveThrowingMutationSession : IModelSession, IModelMutationSession
    {
        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken ct)
            => Task.FromResult(new ModelSummary("savethrow", 1601, 0, 0, 0, 0, 0));
        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken ct)
            => Task.FromResult(new ModelSnapshot("savethrow", 1601, []));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ModelObjectMutationResult AddObject(ModelObjectAddRequest request)
            => new(request.Path, Changed: true);
        public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request) => throw new NotSupportedException();
        public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request) => throw new NotSupportedException();
        public ModelReplaceResult ReplaceText(ModelReplaceRequest request) => throw new NotSupportedException();
        public Task<ModelExportResult> SaveAsync(string? outputPath, string serialization, bool force, CancellationToken ct)
            => throw new IOException("disk full");
    }
}