using Tomix.App.Mv;
using Tomix.App.Set;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

/// <summary>
/// Renaming a measure/column/table breaks DAX that references the old name (live QA: a measure
/// rename silently broke a calculation item, and only the workspace deploy surfaced it). By
/// default set/mv rewrite those references (fixup); --no-fix-refs restores warn-only, and
/// --strict-refs fails on whatever a run leaves broken — everything with fixup off, only
/// unrewritable sites (role RLS) with fixup on.
/// </summary>
public sealed class RenameReferenceFixupTests
{
    [Fact]
    public async Task Set_RenameReferencedMeasure_RewritesReferences()
    {
        var session = NewSession();
        var result = await new SetModelPropertyHandler([new StubProvider(session)]).HandleAsync(
            SetRequest("Sales/Base", [new ModelPropertyAssignment("name", "NewName")]),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Data!.BrokenReferences);
        Assert.Equal(["Sales/Derived"], result.Data.FixedReferences);
        var edit = Assert.Single(session.RewrittenEdits!);
        Assert.Equal("Sales/Derived", edit.Path);
        Assert.Equal("Expression", edit.Property);
        Assert.Equal("[NewName] * 2", edit.Value);
        Assert.True(session.SetPropertyCalled);
    }

    [Fact]
    public async Task Set_RewriteHappensBeforeTheRename()
    {
        // Edits are planned against pre-rename paths, so they must be applied while those
        // paths still resolve.
        var session = NewSession();
        await new SetModelPropertyHandler([new StubProvider(session)]).HandleAsync(
            SetRequest("Sales/Base", [new ModelPropertyAssignment("name", "NewName")]),
            CancellationToken.None);

        Assert.True(session.RewriteCameBeforeSetProperty);
    }

    [Fact]
    public async Task Set_RenameReferencedMeasure_DaxFormPath_RewritesReferences()
    {
        var session = NewSession();
        var result = await new SetModelPropertyHandler([new StubProvider(session)]).HandleAsync(
            SetRequest("'Sales'[Base]", [new ModelPropertyAssignment("name", "NewName")]),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(["Sales/Derived"], result.Data!.FixedReferences);
    }

    [Fact]
    public async Task Set_StrictRefs_PassesWhenEverythingIsFixable()
    {
        var session = NewSession();
        var result = await new SetModelPropertyHandler([new StubProvider(session)]).HandleAsync(
            SetRequest("Sales/Base", [new ModelPropertyAssignment("name", "NewName")], strictRefs: true),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(["Sales/Derived"], result.Data!.FixedReferences);
    }

    [Fact]
    public async Task Set_NoFixRefs_WarnsWithoutRewriting()
    {
        var session = NewSession();
        var result = await new SetModelPropertyHandler([new StubProvider(session)]).HandleAsync(
            SetRequest("Sales/Base", [new ModelPropertyAssignment("name", "NewName")], fixRefs: false),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(["Sales/Derived"], result.Data!.BrokenReferences);
        Assert.Null(result.Data.FixedReferences);
        Assert.Null(session.RewrittenEdits);
        Assert.True(session.SetPropertyCalled);
    }

    [Fact]
    public async Task Set_NoFixRefs_StrictRefs_FailsBeforeMutating()
    {
        var session = NewSession();
        var result = await new SetModelPropertyHandler([new StubProvider(session)]).HandleAsync(
            SetRequest(
                "Sales/Base", [new ModelPropertyAssignment("name", "NewName")],
                strictRefs: true, fixRefs: false),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_RENAME_BREAKS_REFS", result.Diagnostics[0].Code);
        Assert.False(session.SetPropertyCalled);
        Assert.Null(session.RewrittenEdits);
    }

    [Fact]
    public async Task Set_RlsReference_IsUnfixable_WarnsEvenWithFixupOn()
    {
        // Role RLS filters are synthesized per-table in the snapshot and cannot be written
        // back as one property, so they stay a warning.
        var session = SessionWithRlsReference();
        var result = await new SetModelPropertyHandler([new StubProvider(session)]).HandleAsync(
            SetRequest("Sales/Amount", [new ModelPropertyAssignment("name", "NewName")]),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(["Analyst"], result.Data!.BrokenReferences);
        Assert.Null(session.RewrittenEdits);
    }

    [Fact]
    public async Task Set_RlsReference_StrictRefs_FailsBeforeMutating()
    {
        var session = SessionWithRlsReference();
        var result = await new SetModelPropertyHandler([new StubProvider(session)]).HandleAsync(
            SetRequest("Sales/Amount", [new ModelPropertyAssignment("name", "NewName")], strictRefs: true),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_RENAME_BREAKS_REFS", result.Diagnostics[0].Code);
        Assert.False(session.SetPropertyCalled);
    }

    [Fact]
    public async Task Set_RenameUnreferencedMeasure_ReportsNothing()
    {
        var session = NewSession();
        var result = await new SetModelPropertyHandler([new StubProvider(session)]).HandleAsync(
            SetRequest("Sales/Derived", [new ModelPropertyAssignment("name", "NewName")]),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Data!.BrokenReferences);
        Assert.Null(result.Data.FixedReferences);
    }

    [Fact]
    public async Task Set_NonNameProperty_SkipsReferenceCheck()
    {
        var session = NewSession();
        var result = await new SetModelPropertyHandler([new StubProvider(session)]).HandleAsync(
            SetRequest("Sales/Base", [new ModelPropertyAssignment("description", "x")]),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Data!.BrokenReferences);
        Assert.False(session.SnapshotRequested);
    }

    [Fact]
    public async Task Set_CaseOnlyRename_SkipsRewrite()
    {
        // DAX resolves names case-insensitively; rewriting would only churn casing.
        var session = NewSession();
        var result = await new SetModelPropertyHandler([new StubProvider(session)]).HandleAsync(
            SetRequest("Sales/Base", [new ModelPropertyAssignment("name", "BASE")]),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Data!.FixedReferences);
        Assert.Null(session.RewrittenEdits);
    }

    [Fact]
    public async Task Mv_RenameReferencedMeasure_RewritesReferences()
    {
        var session = NewSession();
        var result = await new MoveModelObjectHandler([new StubProvider(session)]).HandleAsync(
            MvRequest("Sales/Base", "Sales/NewName"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(["Sales/Derived"], result.Data!.FixedReferences);
        var edit = Assert.Single(session.RewrittenEdits!);
        Assert.Equal("[NewName] * 2", edit.Value);
    }

    [Fact]
    public async Task Mv_NoFixRefs_WarnsWithoutRewriting()
    {
        var session = NewSession();
        var result = await new MoveModelObjectHandler([new StubProvider(session)]).HandleAsync(
            MvRequest("Sales/Base", "Sales/NewName", fixRefs: false),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(["Sales/Derived"], result.Data!.BrokenReferences);
        Assert.Null(session.RewrittenEdits);
    }

    [Fact]
    public async Task Mv_NoFixRefs_StrictRefs_FailsBeforeMutating()
    {
        var session = NewSession();
        var result = await new MoveModelObjectHandler([new StubProvider(session)]).HandleAsync(
            MvRequest("Sales/Base", "Sales/NewName", strictRefs: true, fixRefs: false),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_RENAME_BREAKS_REFS", result.Diagnostics[0].Code);
        Assert.False(session.SetPropertyCalled);
    }

    [Fact]
    public async Task Set_RenameMeasure_FixesEvenWhenPartitionSharesThePath()
    {
        // Desktop names a table's default partition after the table, so a measure named like its
        // table shares its snapshot path with a partition. The check must not treat that as
        // ambiguous — only DAX-named kinds count.
        var session = new StubSnapshotSession(new ModelSnapshot("M", 1601,
        [
            new ModelObject("Sales", ModelObjectKind.Table, "Sales",
                Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null, Children: []),
            new ModelObject("Sales", ModelObjectKind.Measure, "Sales/Sales",
                Detail: null, Expression: "1", Description: null, Hidden: false, SourceColumn: null, Children: []),
            new ModelObject("Sales", ModelObjectKind.Partition, "Sales/Sales",
                Detail: null, Expression: "let x = 1 in x", Description: null, Hidden: false, SourceColumn: null, Children: []),
            new ModelObject("Derived", ModelObjectKind.Measure, "Sales/Derived",
                Detail: null, Expression: "[Sales] * 2", Description: null, Hidden: false, SourceColumn: null, Children: [])
        ]));

        var result = await new SetModelPropertyHandler([new StubProvider(session)]).HandleAsync(
            SetRequest("'Sales'[Sales]", [new ModelPropertyAssignment("name", "NewName")]),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(["Sales/Derived"], result.Data!.FixedReferences);
    }

    private static SetModelPropertyRequest SetRequest(
        string path,
        IReadOnlyList<ModelPropertyAssignment> properties,
        bool strictRefs = false,
        bool fixRefs = true)
        => new(
            new ModelReference("model.bim"),
            path,
            properties,
            Type: null,
            Save: false,
            SaveTo: null,
            Serialization: "",
            Force: false,
            StrictRefs: strictRefs,
            FixRefs: fixRefs);

    private static MoveModelObjectRequest MvRequest(
        string source, string destination, bool strictRefs = false, bool fixRefs = true)
        => new(
            new ModelReference("model.bim"),
            source,
            destination,
            Type: null,
            Save: false,
            SaveTo: null,
            Serialization: "",
            Force: false,
            StrictRefs: strictRefs,
            FixRefs: fixRefs);

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

    /// <summary>A column referenced only by a role's RLS filter — the unfixable case.</summary>
    private static StubSnapshotSession SessionWithRlsReference()
        => new(new ModelSnapshot("M", 1601,
        [
            new ModelObject("Sales", ModelObjectKind.Table, "Sales",
                Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null, Children: []),
            new ModelObject("Amount", ModelObjectKind.Column, "Sales/Amount",
                Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null, Children: []),
            new ModelObject("Analyst", ModelObjectKind.Role, "Analyst",
                Detail: null, Expression: null, Description: null, Hidden: false, SourceColumn: null, Children: [],
                Properties: new Dictionary<string, string> { ["RlsExpression"] = "'Sales'[Amount] > 0" })
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

        public bool SnapshotRequested { get; private set; }

        public IReadOnlyList<ModelExpressionEdit>? RewrittenEdits { get; private set; }

        public bool RewriteCameBeforeSetProperty { get; private set; }

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
            return new ModelObjectMutationResult(request.Path, Changed: true, Property: request.Properties[^1].Property, Value: request.Properties[^1].Value);
        }

        public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request)
            => new(request.Path, Changed: true);

        public ModelReplaceResult ReplaceText(ModelReplaceRequest request)
            => new(0, []);

        public ModelExpressionRewriteResult RewriteExpressions(IReadOnlyList<ModelExpressionEdit> edits)
        {
            RewrittenEdits = edits;
            RewriteCameBeforeSetProperty = !SetPropertyCalled;
            return new ModelExpressionRewriteResult(edits.Count);
        }

        public Task<ModelExportResult> SaveAsync(string? outputPath, string serialization, bool force, CancellationToken ct)
            => Task.FromResult(new ModelExportResult(outputPath ?? "/local/model", serialization));
    }
}
