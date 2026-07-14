using Tomix.App.Mv;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

/// <summary>
/// mv derives the new name from the destination path. Live-model QA showed the old
/// slash-only/apostrophe-stripping derivation corrupting names: a DAX-form destination
/// (<c>Sales[New]</c>) became the literal object name, and apostrophes were silently
/// dropped. Both paths must parse with the same rules the mutation resolver uses.
/// </summary>
public sealed class MoveModelObjectHandlerTests
{
    [Fact]
    public async Task DaxFormDestination_RenamesToLeafName_NotTheBracketString()
    {
        var session = NewSession();
        var result = await Handle(session, "Sales[Base]", "Sales[Renamed]");

        Assert.True(result.Success);
        Assert.Equal("Renamed", session.LastSetValue);
        Assert.Equal("Sales/Base", result.Data!.Moved);
        Assert.Equal("Sales/Renamed", result.Data.To);
    }

    [Fact]
    public async Task DaxFormWithEscapedApostrophe_KeepsApostropheInParentAndLeaf()
    {
        var session = NewSession();
        var result = await Handle(session, "'KPI''er'[Base]", "'KPI''er'[QA's Name]");

        Assert.True(result.Success);
        Assert.Equal("QA's Name", session.LastSetValue);
        Assert.Equal("KPI'er/Base", result.Data!.Moved);
    }

    [Fact]
    public async Task ApostropheInDestinationLeaf_IsPreserved()
    {
        var session = NewSession();
        var result = await Handle(session, "Sales/Base", "Sales/QA's Measure");

        Assert.True(result.Success);
        Assert.Equal("QA's Measure", session.LastSetValue);
        Assert.Equal("Sales/QA's Measure", result.Data!.To);
    }

    [Fact]
    public async Task ContainerKeywordPaths_RenameToLeafName()
    {
        var session = NewSession();
        var result = await Handle(session, "tables/Sales/measures/Base", "tables/Sales/measures/New");

        Assert.True(result.Success);
        Assert.Equal("New", session.LastSetValue);
    }

    [Fact]
    public async Task DestinationLeaf_IsWhitespaceTrimmed()
    {
        var session = NewSession();
        var result = await Handle(session, "Sales/Base", "Sales/  padded  ");

        Assert.True(result.Success);
        Assert.Equal("padded", session.LastSetValue);
    }

    [Fact]
    public async Task CrossParentMove_Fails_WithoutMutating()
    {
        var session = NewSession();
        var result = await Handle(session, "Sales/Base", "Other/Base2");

        Assert.False(result.Success);
        Assert.Equal("TOMIX_MOVE_UNSUPPORTED", result.Diagnostics[0].Code);
        Assert.False(session.SetPropertyCalled);
    }

    [Theory]
    [InlineData("", "Sales/New")]
    [InlineData("   ", "Sales/New")]
    [InlineData("Sales/Base", "Sales/")]
    [InlineData("Sales/Base", "Sales/   ")]
    [InlineData("Sales/Base", "")]
    public async Task MissingObjectName_IsAUsageError_NotACrossParentError(string source, string destination)
    {
        var session = NewSession();
        var result = await Handle(session, source, destination);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_MOVE_INVALID_PATH", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
        Assert.False(session.SetPropertyCalled);
    }

    [Fact]
    public async Task SameSourceAndDestination_IsANoOp_NotARename()
    {
        var session = NewSession();
        var result = await Handle(session, "Sales/Base", "Sales/Base");

        Assert.False(result.Success);
        Assert.Equal("TOMIX_MOVE_NOOP", result.Diagnostics[0].Code);
        Assert.False(session.SetPropertyCalled);
    }

    [Fact]
    public async Task CaseOnlyRename_Proceeds_AndSkipsReferenceCheck()
    {
        // DAX resolves names case-insensitively, so a case-only rename breaks nothing —
        // warning about Sales/Derived here would be a false positive.
        var session = NewSession();
        var result = await Handle(session, "Sales/Base", "Sales/BASE");

        Assert.True(result.Success);
        Assert.Equal("BASE", session.LastSetValue);
        Assert.Null(result.Data!.BrokenReferences);
        Assert.False(session.SnapshotRequested);
    }

    [Fact]
    public async Task RevertWithNothingStaged_Fails_InsteadOfClaimingSuccess()
    {
        var session = NewSession();
        var request = new MoveModelObjectRequest(
            new ModelReference($"/nonexistent/{Guid.NewGuid():N}.bim"),
            "x", "x", Type: null,
            Save: false, SaveTo: null, Serialization: "", Force: false,
            Revert: true);

        var result = await new MoveModelObjectHandler([new StubProvider(session)])
            .HandleAsync(request, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_STAGE_NOTHING_STAGED", result.Diagnostics[0].Code);
    }

    private static Task<Core.Results.TomixResult<MoveModelObjectResult>> Handle(
        StubSnapshotSession session, string source, string destination)
        => new MoveModelObjectHandler([new StubProvider(session)]).HandleAsync(
            new MoveModelObjectRequest(
                new ModelReference("model.bim"),
                source, destination, Type: null,
                Save: false, SaveTo: null, Serialization: "", Force: false),
            CancellationToken.None);

    /// <summary>Two measures on table Sales: Derived's DAX references Base via [Base].</summary>
    private static StubSnapshotSession NewSession()
        => new(new ModelSnapshot("M", 1601,
        [
            new ModelObject("Sales", ModelObjectKind.Table, "Sales",
                Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null, Children: []),
            new ModelObject("Base", ModelObjectKind.Measure, "Sales/Base",
                Detail: null, Expression: "1", Description: null, Hidden: false, SourceColumn: null, Children: []),
            new ModelObject("Derived", ModelObjectKind.Measure, "Sales/Derived",
                Detail: null, Expression: "[Base] * 2", Description: null, Hidden: false, SourceColumn: null, Children: [])
        ]));

    private sealed class StubProvider : IModelProvider
    {
        private readonly StubSnapshotSession _session;

        public StubProvider(StubSnapshotSession session) => _session = session;

        public bool CanOpen(ModelReference _) => true;

        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken ct)
            => Task.FromResult<IModelSession>(_session);
    }

    private sealed class StubSnapshotSession : IModelSession, IModelMutationSession
    {
        private readonly ModelSnapshot _snapshot;

        public StubSnapshotSession(ModelSnapshot snapshot) => _snapshot = snapshot;

        public bool SetPropertyCalled { get; private set; }

        public string? LastSetValue { get; private set; }

        public bool SnapshotRequested { get; private set; }

        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 1, 2, 0, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
        {
            SnapshotRequested = true;
            return Task.FromResult(_snapshot);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ModelObjectMutationResult AddObject(ModelObjectAddRequest request)
            => new(request.Path, Changed: true);

        public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request)
        {
            SetPropertyCalled = true;
            LastSetValue = request.Properties[^1].Value;
            return new ModelObjectMutationResult(request.Path, Changed: true, Property: request.Properties[^1].Property, Value: request.Properties[^1].Value);
        }

        public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request)
            => new(request.Path, Changed: true);

        public ModelReplaceResult ReplaceText(ModelReplaceRequest request)
            => new(0, []);

        public Task<ModelExportResult> SaveAsync(string? outputPath, string serialization, bool force, CancellationToken ct)
            => Task.FromResult(new ModelExportResult(outputPath ?? "/local/model", serialization));
    }
}
