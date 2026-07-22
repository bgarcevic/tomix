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

    private static Tomix.App.Mutations.MutationStores TestStores => new(
        new Tomix.App.State.StagingStore(
            Path.Combine(Path.GetTempPath(), $"tomix-tests-{Guid.NewGuid():N}"), "test-session"),
        () => null);
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
    public async Task CrossTableMove_WithoutMoveCapability_Fails_WithoutMutating()
    {
        var session = NewSession();
        var result = await Handle(session, "Sales/Base", "Other/Base2");

        Assert.False(result.Success);
        Assert.Equal("TOMIX_MUTATION_UNSUPPORTED", result.Diagnostics[0].Code);
        Assert.False(session.SetPropertyCalled);
    }

    [Fact]
    public async Task CrossTableMove_CallsMoveObject_NotSetProperty()
    {
        var session = NewMoveSession();
        var result = await Handle(session, "Sales/Base", "Metrics/Base");

        Assert.True(result.Success);
        Assert.NotNull(session.LastMove);
        Assert.Equal("Metrics", session.LastMove!.NewParent);
        Assert.Equal("Base", session.LastMove.NewName);
        Assert.False(session.SetPropertyCalled);
        Assert.Equal("Metrics/Base", result.Data!.To);
    }

    [Fact]
    public async Task CrossTableMove_WithRename_PassesNewNameToMoveObject()
    {
        var session = NewMoveSession();
        var result = await Handle(session, "Sales/Base", "Metrics/Renamed");

        Assert.True(result.Success);
        Assert.Equal("Metrics", session.LastMove!.NewParent);
        Assert.Equal("Renamed", session.LastMove.NewName);
    }

    [Fact]
    public async Task CrossTableMove_RewritesQualifiedReference_LeavesUnqualifiedAlone()
    {
        // Qualified's DAX names the home table ('Sales'[Base]) and breaks on move; Derived's
        // unqualified [Base] stays valid and must not be touched or reported.
        var session = NewMoveSession();
        var result = await Handle(session, "Sales/Base", "Metrics/Base");

        Assert.True(result.Success);
        var edit = Assert.Single(session.LastRewrites!);
        Assert.Equal("Sales/Qualified", edit.Path);
        Assert.Equal("'Metrics'[Base] + 0", edit.Value);
        Assert.Equal(["Sales/Qualified"], result.Data!.FixedReferences);
    }

    [Fact]
    public async Task CrossTableMove_WithFolderDestination_PassesDisplayFolder()
    {
        var session = NewMoveSession();
        var result = await Handle(session, "Sales/Base", "Metrics/Sub/Base2");

        Assert.True(result.Success);
        Assert.Equal("Metrics", session.LastMove!.NewParent);
        Assert.Equal("Base2", session.LastMove.NewName);
        Assert.Equal("Sub", session.LastMove.NewDisplayFolder);
        Assert.Equal("Metrics/Sub/Base2", result.Data!.To);
    }

    [Fact]
    public async Task CrossTableMove_WithoutFolderSegments_KeepsTheFolderItHad()
    {
        var session = NewMoveSession();
        var result = await Handle(session, "Sales/Base", "Metrics/Base");

        Assert.True(result.Success);
        Assert.Null(session.LastMove!.NewDisplayFolder);
    }

    [Theory]
    [InlineData("", "Sales/New")]
    [InlineData("   ", "Sales/New")]
    [InlineData("Sales/", "Sales/New")]
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

    [Theory]
    [InlineData("Sales/")]
    [InlineData("Sales/   ")]
    public async Task TrailingSlashDestination_KeepsTheName_AndNoOpsWhenNothingChanges(string destination)
    {
        // 'Sales/' means "keep the name" — with no folder in play there is nothing to do.
        var session = NewSession();
        var result = await Handle(session, "Sales/Base", destination);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_MOVE_NOOP", result.Diagnostics[0].Code);
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
        Assert.Null(session.LastRewrites);
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

        var result = await new MoveModelObjectHandler([new StubProvider(session)], TestStores)
            .HandleAsync(request, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_STAGE_NOTHING_STAGED", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task FolderDestination_SameTable_SetsDisplayFolder_WithoutReferenceFixup()
    {
        var session = NewFolderSession();
        var result = await Handle(session, "Sales/Plain", "Sales/Finance/Plain");

        Assert.True(result.Success);
        var assignment = Assert.Single(session.LastSetProperties!);
        Assert.Equal("displayFolder", assignment.Property);
        Assert.Equal("Finance", assignment.Value);
        Assert.Equal("Sales/Finance/Plain", result.Data!.To);
        Assert.Null(session.LastRewrites);
    }

    [Fact]
    public async Task FolderQualifiedSource_MovesOutOfTheFolder()
    {
        var session = NewFolderSession();
        var result = await Handle(session, "Sales/Finance/Base", "Sales/Base");

        Assert.True(result.Success);
        var assignment = Assert.Single(session.LastSetProperties!);
        Assert.Equal("displayFolder", assignment.Property);
        Assert.Equal("", assignment.Value);
    }

    [Fact]
    public async Task NestedFolders_JoinWithBackslash_AndCombineWithRename()
    {
        var session = NewFolderSession();
        var result = await Handle(session, "Sales/Plain", "Sales/Finance/KPIs/Plain2");

        Assert.True(result.Success);
        Assert.Equal(2, session.LastSetProperties!.Count);
        Assert.Equal("displayFolder", session.LastSetProperties[0].Property);
        Assert.Equal(@"Finance\KPIs", session.LastSetProperties[0].Value);
        Assert.Equal("name", session.LastSetProperties[1].Property);
        Assert.Equal("Plain2", session.LastSetProperties[1].Value);
    }

    [Fact]
    public async Task TrailingSlashDestination_MovesBetweenFolders_KeepingTheName()
    {
        var session = NewFolderSession();
        var result = await Handle(session, "Sales/Finance/Base", "Sales/Margins/");

        Assert.True(result.Success);
        var assignment = Assert.Single(session.LastSetProperties!);
        Assert.Equal("displayFolder", assignment.Property);
        Assert.Equal("Margins", assignment.Value);
        Assert.Equal("Sales/Margins/Base", result.Data!.To);
    }

    [Fact]
    public async Task PlainRename_DoesNotTouchTheExistingFolder()
    {
        var session = NewFolderSession();
        var result = await Handle(session, "Sales/Base", "Sales/Base2");

        Assert.True(result.Success);
        var assignment = Assert.Single(session.LastSetProperties!);
        Assert.Equal("name", assignment.Property);
    }

    [Fact]
    public async Task SourceFolderMismatch_IsNotFound_AndReportsTheActualFolder()
    {
        var session = NewFolderSession();
        var result = await Handle(session, "Sales/Wrong/Base", "Sales/Base");

        Assert.False(result.Success);
        Assert.Equal("TOMIX_OBJECT_NOT_FOUND", result.Diagnostics[0].Code);
        Assert.False(session.SetPropertyCalled);
    }

    [Fact]
    public async Task FolderMove_OnKindWithoutDisplayFolders_IsUnsupported()
    {
        var session = NewFolderSession();
        var result = await Handle(session, "Roles/Admin", "Roles/Sub/Admin");

        Assert.False(result.Success);
        Assert.Equal("TOMIX_MUTATION_UNSUPPORTED", result.Diagnostics[0].Code);
        Assert.False(session.SetPropertyCalled);
    }

    [Fact]
    public async Task LevelRename_WithinItsHierarchy_StillWorks()
    {
        // A 3-segment path is a level when one exists there — levels win over a folder reading.
        var session = NewFolderSession();
        var result = await Handle(session, "Sales/Calendar/Year", "Sales/Calendar/CalendarYear");

        Assert.True(result.Success);
        Assert.Equal("CalendarYear", session.LastSetValue);
    }

    [Fact]
    public async Task LevelMove_ToAnotherHierarchy_IsUnsupported()
    {
        var session = NewFolderSession();
        var result = await Handle(session, "Sales/Calendar/Year", "Sales/Fiscal/Year");

        Assert.False(result.Success);
        Assert.Equal("TOMIX_MUTATION_UNSUPPORTED", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task SameNameMeasureAndHierarchy_WithoutType_IsAmbiguous()
    {
        var session = NewFolderSession();
        var result = await Handle(session, "Sales/Date", "Sales/Archive/Date");

        Assert.False(result.Success);
        Assert.Equal("TOMIX_OBJECT_AMBIGUOUS", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task SameNameMeasureAndHierarchy_WithType_DisambiguatesTheFolderMove()
    {
        var session = NewFolderSession();
        var result = await Handle(session, "Sales/Date", "Sales/Archive/Date", ModelObjectKind.Hierarchy);

        Assert.True(result.Success);
        var assignment = Assert.Single(session.LastSetProperties!);
        Assert.Equal("displayFolder", assignment.Property);
        Assert.Equal("Archive", assignment.Value);
    }

    [Fact]
    public async Task MoveToTheFolderItIsAlreadyIn_IsANoOp()
    {
        var session = NewFolderSession();
        var result = await Handle(session, "Sales/Base", "Sales/Finance/Base");

        Assert.False(result.Success);
        Assert.Equal("TOMIX_MOVE_NOOP", result.Diagnostics[0].Code);
        Assert.False(session.SetPropertyCalled);
    }

    private static Task<Core.Results.TomixResult<MoveModelObjectResult>> Handle(
        StubSnapshotSession session, string source, string destination, ModelObjectKind? type = null)
        => new MoveModelObjectHandler([new StubProvider(session)], TestStores).HandleAsync(
            new MoveModelObjectRequest(
                new ModelReference("model.bim"),
                source, destination, Type: type,
                Save: false, SaveTo: null, Serialization: "", Force: false),
            CancellationToken.None);

    /// <summary>Move-capable session over Sales with an extra measure whose DAX references
    /// Base fully qualified — the one reference shape a cross-table move breaks.</summary>
    private static MoveCapableStubSession NewMoveSession()
        => new(new ModelSnapshot("M", 1601,
        [
            new ModelObject("Sales", ModelObjectKind.Table, "Sales",
                Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null, Children: []),
            new ModelObject("Metrics", ModelObjectKind.Table, "Metrics",
                Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null, Children: []),
            new ModelObject("Base", ModelObjectKind.Measure, "Sales/Base",
                Detail: null, Expression: "1", Description: null, Hidden: false, SourceColumn: null, Children: []),
            new ModelObject("Derived", ModelObjectKind.Measure, "Sales/Derived",
                Detail: null, Expression: "[Base] * 2", Description: null, Hidden: false, SourceColumn: null, Children: []),
            new ModelObject("Qualified", ModelObjectKind.Measure, "Sales/Qualified",
                Detail: null, Expression: "'Sales'[Base] + 0", Description: null, Hidden: false, SourceColumn: null, Children: [])
        ]));

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

    /// <summary>Display-folder scenarios: Base sits in folder 'Finance', Plain in no folder,
    /// hierarchy Calendar has level Year, measure and hierarchy share the name 'Date', and
    /// role Admin covers kinds without folders.</summary>
    private static StubSnapshotSession NewFolderSession()
        => new(new ModelSnapshot("M", 1601,
        [
            new ModelObject("Sales", ModelObjectKind.Table, "Sales",
                Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null, Children: []),
            new ModelObject("Base", ModelObjectKind.Measure, "Sales/Base",
                Detail: null, Expression: "1", Description: null, Hidden: false, SourceColumn: null, Children: [],
                Properties: new Dictionary<string, string> { ["DisplayFolder"] = "Finance" }),
            new ModelObject("Plain", ModelObjectKind.Measure, "Sales/Plain",
                Detail: null, Expression: "2", Description: null, Hidden: false, SourceColumn: null, Children: [],
                Properties: new Dictionary<string, string> { ["DisplayFolder"] = "" }),
            new ModelObject("Calendar", ModelObjectKind.Hierarchy, "Sales/Calendar",
                Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null,
                Children:
                [
                    new ModelObject("Year", ModelObjectKind.Level, "Sales/Calendar/Year",
                        Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null, Children: [])
                ]),
            new ModelObject("Date", ModelObjectKind.Measure, "Sales/Date",
                Detail: null, Expression: "3", Description: null, Hidden: false, SourceColumn: null, Children: []),
            new ModelObject("Date", ModelObjectKind.Hierarchy, "Sales/Date",
                Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null, Children: []),
            new ModelObject("Admin", ModelObjectKind.Role, "Roles/Admin",
                Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null, Children: [])
        ]));

    private sealed class StubProvider : IModelProvider
    {
        private readonly StubSnapshotSession _session;

        public StubProvider(StubSnapshotSession session) => _session = session;

        public bool CanOpen(ModelReference _) => true;

        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken ct)
            => Task.FromResult<IModelSession>(_session);
    }

    private sealed class MoveCapableStubSession : StubSnapshotSession, IObjectMoveSession
    {
        public MoveCapableStubSession(ModelSnapshot snapshot)
            : base(snapshot)
        {
        }

        public ModelObjectMoveRequest? LastMove { get; private set; }

        public ModelObjectMutationResult MoveObject(ModelObjectMoveRequest request)
        {
            LastMove = request;
            return new ModelObjectMutationResult($"{request.NewParent}/{request.NewName}", Changed: true);
        }
    }

    private class StubSnapshotSession : IModelSession, IModelMutationSession, IExpressionRewriteSession
    {
        private readonly ModelSnapshot _snapshot;

        public StubSnapshotSession(ModelSnapshot snapshot) => _snapshot = snapshot;

        public bool SetPropertyCalled { get; private set; }

        public string? LastSetValue { get; private set; }

        public IReadOnlyList<ModelPropertyAssignment>? LastSetProperties { get; private set; }

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
            LastSetProperties = request.Properties;
            return new ModelObjectMutationResult(request.Path, Changed: true, Property: request.Properties[^1].Property, Value: request.Properties[^1].Value);
        }

        public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request)
            => new(request.Path, Changed: true);

        public ModelReplaceResult ReplaceText(ModelReplaceRequest request)
            => new(0, []);

        public IReadOnlyList<ModelExpressionEdit>? LastRewrites { get; private set; }

        public ModelExpressionRewriteResult RewriteExpressions(IReadOnlyList<ModelExpressionEdit> edits)
        {
            LastRewrites = edits;
            return new(edits.Count);
        }

        public Task<ModelExportResult> SaveAsync(string? outputPath, string serialization, bool force, CancellationToken ct)
            => Task.FromResult(new ModelExportResult(outputPath ?? "/local/model", serialization));
    }
}
