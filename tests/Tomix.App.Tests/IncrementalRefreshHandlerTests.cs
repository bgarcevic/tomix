using Tomix.App.IncrementalRefresh;
using Tomix.App.State;
using Tomix.Core.Authentication;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;

namespace Tomix.App.Tests;

public sealed class IncrementalRefreshHandlerTests
{
    private const string ValidSourceExpression =
        "let Source = Src, Filtered = Table.SelectRows(Source, each [Date] >= RangeStart and [Date] < RangeEnd) in Filtered";

    // ---- show ----

    [Fact]
    public async Task Show_NoProvider_ReturnsNoProviderError()
    {
        var result = await new ShowRefreshPolicyHandler([]).HandleAsync(
            new ShowRefreshPolicyRequest(new ModelReference("nonexistent.tmdl"), "Sales"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_NO_PROVIDER", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task Show_NoPolicy_ReturnsNotFound()
    {
        var result = await new ShowRefreshPolicyHandler([new StubMutationProvider(new StubMutationSession(policy: null))])
            .HandleAsync(new ShowRefreshPolicyRequest(new ModelReference("any"), "Sales"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_REFRESH_POLICY_NOT_FOUND", result.Diagnostics[0].Code);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Show_MissingTable_ReturnsObjectNotFound()
    {
        var session = new StubMutationSession(policy: null) { ThrowNotFound = true };
        var result = await new ShowRefreshPolicyHandler([new StubMutationProvider(session)])
            .HandleAsync(new ShowRefreshPolicyRequest(new ModelReference("any"), "Bogus"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_OBJECT_NOT_FOUND", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task Show_RemoteOpenAuthFailure_ReturnsAuthRequired()
    {
        // OpenAsync throwing on a remote endpoint must be caught and rendered as an actionable
        // diagnostic, not bubble out as an unhandled exception.
        var handler = new ShowRefreshPolicyHandler([new ThrowingProvider()]);
        var result = await handler.HandleAsync(
            new ShowRefreshPolicyRequest(new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/ws", "MyModel"), "Sales"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_AUTH_REQUIRED", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task Show_ReturnsPolicy()
    {
        var policy = SamplePolicy();
        var result = await new ShowRefreshPolicyHandler([new StubMutationProvider(new StubMutationSession(policy))])
            .HandleAsync(new ShowRefreshPolicyRequest(new ModelReference("any"), "Sales"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Sales", result.Data!.Table);
        Assert.Equal(10, result.Data.RollingWindowPeriods);
    }

    [Fact]
    public async Task Show_SessionWithoutRefreshPolicyCapability_ReturnsMutationUnsupported()
    {
        // A mutation session that does not implement IRefreshPolicyMutationSession must map to
        // the same diagnostic the old default interface members produced.
        var result = await new ShowRefreshPolicyHandler([new StubMutationProvider(new NoPolicyCapabilitySession())])
            .HandleAsync(new ShowRefreshPolicyRequest(new ModelReference("any"), "Sales"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_MUTATION_UNSUPPORTED", result.Diagnostics[0].Code);
    }

    // ---- set (validation-only, before MutationRunner) ----

    [Fact]
    public async Task Set_NoOptions_ReturnsNoOptionsError()
    {
        var result = await new SetRefreshPolicyHandler([]).HandleAsync(
            NewSetRequest(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_REFRESH_POLICY_NO_OPTIONS", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task Set_RevertWithOptions_ReturnsConflict()
    {
        var result = await new SetRefreshPolicyHandler([]).HandleAsync(
            NewSetRequest(revert: true, incrementalPeriods: 3), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_STAGE_OPTIONS_CONFLICT", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    // ---- apply ----

    [Fact]
    public async Task Apply_LocalTarget_ReturnsNoRemoteTarget()
    {
        var handler = new ApplyRefreshPolicyHandler([], () => null);
        var result = await handler.HandleAsync(
            new ApplyRefreshPolicyRequest("./local.tmdl", null, null, "Sales", null, Refresh: true, null),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_REFRESH_NO_REMOTE_TARGET", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task Apply_BareWorkspaceName_IsAcceptedAsRemoteTarget()
    {
        // -s MyWorkspace (a bare workspace name, no scheme) must be normalized to an XMLA
        // endpoint and accepted as a remote target, not rejected as non-remote. With no provider
        // registered it falls through to TOMIX_NO_PROVIDER — proving it cleared the remote gate.
        var handler = new ApplyRefreshPolicyHandler([], () => null);
        var result = await handler.HandleAsync(
            new ApplyRefreshPolicyRequest(null, "MyWorkspace", "Model", "Sales", null, Refresh: true, null),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_NO_PROVIDER", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task Apply_NonRefreshSession_ReturnsUnsupported()
    {
        var handler = new ApplyRefreshPolicyHandler(
            [new StubMutationProvider(new StubMutationSession(policy: null))],
            () => RemoteSession());
        var result = await handler.HandleAsync(
            new ApplyRefreshPolicyRequest(null, null, "MyModel", "Sales", null, Refresh: true, null),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_REFRESH_POLICY_UNSUPPORTED", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task Apply_HappyPath_ReturnsResult()
    {
        var session = new StubApplySession();
        var handler = new ApplyRefreshPolicyHandler([new StubApplyProvider(session)], () => RemoteSession());
        var result = await handler.HandleAsync(
            new ApplyRefreshPolicyRequest(null, null, "MyModel", "Sales", new DateOnly(2024, 6, 1), Refresh: false, 4),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Sales", result.Data!.Table);
        Assert.False(result.Data.Refreshed);
        Assert.True(session.ApplyCalled);
    }

    [Fact]
    public async Task Apply_NoPolicy_ReturnsPolicyNotFound()
    {
        var session = new StubApplySession(
            new RefreshPolicyNotFoundException("Table 'Sales' has no incremental refresh policy."));
        var handler = new ApplyRefreshPolicyHandler([new StubApplyProvider(session)], () => RemoteSession());
        var result = await handler.HandleAsync(
            new ApplyRefreshPolicyRequest(null, null, "MyModel", "Sales", null, Refresh: true, null),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_REFRESH_POLICY_NOT_FOUND", result.Diagnostics[0].Code);
    }

    [Fact]
    public async Task Apply_ServerFailure_ReturnsApplyFailed_NotPolicyNotFound()
    {
        // A generic InvalidOperationException (e.g. bad --database from OpenAsync, or a server-side
        // apply rejection) must not be mislabeled as a missing policy.
        var session = new StubApplySession(new InvalidOperationException("Multiple databases on the endpoint."));
        var handler = new ApplyRefreshPolicyHandler([new StubApplyProvider(session)], () => RemoteSession());
        var result = await handler.HandleAsync(
            new ApplyRefreshPolicyRequest(null, null, "MyModel", "Sales", null, Refresh: true, null),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_REFRESH_POLICY_APPLY_FAILED", result.Diagnostics[0].Code);
    }

    // ---- end-to-end round-trip through the real TMDL provider + MutationRunner ----

    [Fact]
    public async Task Set_Show_Rm_RoundTripThroughRealProvider()
    {
        var model = CopySample();
        try
        {
            var providers = new IModelProvider[] { new TmdlModelProvider() };
            var reference = new ModelReference(model);

            var setResult = await new SetRefreshPolicyHandler(providers).HandleAsync(
                NewSetRequest(reference,
                    rollingWindowPeriods: 10, rollingWindowGranularity: "year",
                    incrementalPeriods: 3, incrementalGranularity: "day",
                    sourceExpression: ValidSourceExpression, save: true),
                CancellationToken.None);

            Assert.True(setResult.Success, string.Join("; ", setResult.Diagnostics.Select(d => d.Message)));
            Assert.True(setResult.Data!.Created);
            Assert.Equal(["RangeStart", "RangeEnd"], setResult.Data.CreatedExpressions);

            // Reopen from disk and confirm the policy persisted.
            var showResult = await new ShowRefreshPolicyHandler(providers).HandleAsync(
                new ShowRefreshPolicyRequest(reference, "Sales"), CancellationToken.None);
            Assert.True(showResult.Success);
            Assert.Equal(10, showResult.Data!.RollingWindowPeriods);
            Assert.Equal("Year", showResult.Data.RollingWindowGranularity);

            var rmResult = await new RemoveRefreshPolicyHandler(providers).HandleAsync(
                new RemoveRefreshPolicyRequest(reference, "Sales", IfExists: false,
                    Save: true, SaveTo: null, Serialization: "", Force: false, NoSync: true),
                CancellationToken.None);
            Assert.True(rmResult.Success);
            Assert.Equal("Sales", rmResult.Data!.Removed);

            var afterRm = await new ShowRefreshPolicyHandler(providers).HandleAsync(
                new ShowRefreshPolicyRequest(reference, "Sales"), CancellationToken.None);
            Assert.False(afterRm.Success);
            Assert.Equal("TOMIX_REFRESH_POLICY_NOT_FOUND", afterRm.Diagnostics[0].Code);
        }
        finally
        {
            Directory.Delete(model, recursive: true);
        }
    }

    [Fact]
    public async Task Set_InvalidWithoutForce_ReturnsPolicyInvalid()
    {
        var model = CopySample();
        try
        {
            var providers = new IModelProvider[] { new TmdlModelProvider() };
            var result = await new SetRefreshPolicyHandler(providers).HandleAsync(
                NewSetRequest(new ModelReference(model),
                    rollingWindowPeriods: 10, rollingWindowGranularity: "year",
                    incrementalPeriods: 3, incrementalGranularity: "day",
                    sourceExpression: "let Source = Src in Source", save: true),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("TOMIX_REFRESH_POLICY_INVALID", result.Diagnostics[0].Code);
        }
        finally
        {
            Directory.Delete(model, recursive: true);
        }
    }

    [Fact]
    public async Task Set_UnknownTable_ReturnsObjectNotFound()
    {
        var model = CopySample();
        try
        {
            var providers = new IModelProvider[] { new TmdlModelProvider() };
            var result = await new SetRefreshPolicyHandler(providers).HandleAsync(
                NewSetRequest(new ModelReference(model),
                    rollingWindowPeriods: 10, rollingWindowGranularity: "year",
                    incrementalPeriods: 3, incrementalGranularity: "day",
                    sourceExpression: ValidSourceExpression, save: true, table: "Bogus"),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("TOMIX_OBJECT_NOT_FOUND", result.Diagnostics[0].Code);
        }
        finally
        {
            Directory.Delete(model, recursive: true);
        }
    }

    [Fact]
    public async Task Set_ForcedInvalid_KeepsErrorsInResult()
    {
        var model = CopySample();
        try
        {
            var providers = new IModelProvider[] { new TmdlModelProvider() };
            var result = await new SetRefreshPolicyHandler(providers).HandleAsync(
                NewSetRequest(new ModelReference(model),
                    rollingWindowPeriods: 10, rollingWindowGranularity: "year",
                    incrementalPeriods: 3, incrementalGranularity: "day",
                    sourceExpression: "let Source = Src in Source", save: true, force: true),
                CancellationToken.None);

            // --force overrode the blocking error; the save succeeds but the error must remain
            // visible in the result rather than being silently dropped.
            Assert.True(result.Success);
            Assert.NotNull(result.Data!.Issues);
            Assert.Contains(result.Data.Issues!, i => i.IsError && i.Code == "source_expression_range_refs");
        }
        finally
        {
            Directory.Delete(model, recursive: true);
        }
    }

    [Fact]
    public async Task Rm_NoPolicyWithoutIfExists_ReturnsPolicyNotFound()
    {
        var model = CopySample();
        try
        {
            var providers = new IModelProvider[] { new TmdlModelProvider() };
            // The sample's Sales table has no policy; rm without --if-exists must emit the
            // documented code, not the generic TOMIX_MUTATION_FAILED.
            var result = await new RemoveRefreshPolicyHandler(providers).HandleAsync(
                new RemoveRefreshPolicyRequest(new ModelReference(model), "Sales", IfExists: false,
                    Save: true, SaveTo: null, Serialization: "", Force: false, NoSync: true),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("TOMIX_REFRESH_POLICY_NOT_FOUND", result.Diagnostics[0].Code);
        }
        finally
        {
            Directory.Delete(model, recursive: true);
        }
    }

    private static SetRefreshPolicyRequest NewSetRequest(
        ModelReference? model = null,
        string table = "Sales",
        int? rollingWindowPeriods = null,
        string? rollingWindowGranularity = null,
        int? incrementalPeriods = null,
        string? incrementalGranularity = null,
        string? sourceExpression = null,
        bool save = false,
        bool revert = false,
        bool force = false)
        => new(
            model ?? new ModelReference("model.bim"),
            table,
            Mode: null,
            rollingWindowGranularity,
            rollingWindowPeriods,
            incrementalGranularity,
            incrementalPeriods,
            IncrementalOffset: null,
            PollingExpression: null,
            sourceExpression,
            Force: force,
            Save: save,
            SaveTo: null,
            Serialization: "",
            Stage: false,
            Revert: revert,
            NoSync: true);

    private static RefreshPolicyInfo SamplePolicy() => new(
        "Sales", "Import", "Year", 10, "Day", 3, 0, "", ValidSourceExpression, [], []);

    private static string CopySample()
    {
        var dest = Path.Combine(Path.GetTempPath(), $"tomix-ir-test-{Guid.NewGuid():N}");
        CopyDirectory(LocateSample(), dest);
        return dest;
    }

    private static string LocateSample()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "basic-tmdl");
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new InvalidOperationException("samples/basic-tmdl not found above test base directory.");
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    private static CliConnectionState RemoteSession() =>
        new(Server: "powerbi://api.powerbi.com/v1.0/myorg/ws",
            Database: "MyModel",
            Model: null,
            Auth: null,
            Local: false,
            Profile: null,
            Workspace: null);

    private sealed class StubMutationProvider(IModelSession session) : IModelProvider
    {
        public bool CanOpen(ModelReference reference) => true;
        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken __)
            => Task.FromResult<IModelSession>(session);
    }

    private sealed class StubMutationSession(RefreshPolicyInfo? policy)
        : IModelSession, IModelMutationSession, IRefreshPolicyMutationSession
    {
        public bool ThrowNotFound { get; init; }
        public string SourcePath => "";
        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 0, 0, 0, 0, 0));
        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(new ModelSnapshot("stub", 1601, []));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public RefreshPolicyInfo? GetRefreshPolicy(string table)
            => ThrowNotFound ? throw new ObjectNotFoundException($"Table not found: {table}") : policy;

        public RefreshPolicySetResult SetRefreshPolicy(RefreshPolicySetRequest request) => throw new NotSupportedException();
        public ModelObjectMutationResult RemoveRefreshPolicy(string table, bool ifExists = false) => throw new NotSupportedException();

        public ModelObjectMutationResult AddObject(ModelObjectAddRequest request) => throw new NotSupportedException();
        public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request) => throw new NotSupportedException();
        public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request) => throw new NotSupportedException();
        public ModelReplaceResult ReplaceText(ModelReplaceRequest request) => throw new NotSupportedException();
        public Task<ModelExportResult> SaveAsync(string? outputPath, string serialization, bool force, CancellationToken ct)
            => Task.FromResult(new ModelExportResult("stub", "stub"));
    }

    private sealed class NoPolicyCapabilitySession : IModelSession, IModelMutationSession
    {
        public string SourcePath => "";
        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 0, 0, 0, 0, 0));
        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(new ModelSnapshot("stub", 1601, []));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ModelObjectMutationResult AddObject(ModelObjectAddRequest request) => throw new NotSupportedException();
        public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request) => throw new NotSupportedException();
        public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request) => throw new NotSupportedException();
        public ModelReplaceResult ReplaceText(ModelReplaceRequest request) => throw new NotSupportedException();
        public Task<ModelExportResult> SaveAsync(string? outputPath, string serialization, bool force, CancellationToken ct)
            => Task.FromResult(new ModelExportResult("stub", "stub"));
    }

    private sealed class ThrowingProvider : IModelProvider
    {
        public bool CanOpen(ModelReference reference) => true;
        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken __)
            => throw new AuthenticationRequiredException("Not authenticated. Run 'tx auth login'.");
    }

    private sealed class StubApplyProvider(StubApplySession session) : IModelProvider
    {
        public bool CanOpen(ModelReference reference) => reference.IsRemote;
        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken __)
            => Task.FromResult<IModelSession>(session);
    }

    private sealed class StubApplySession(Exception? throwOnApply = null) : IModelSession, IModelRefreshSession
    {
        public bool ApplyCalled { get; private set; }
        public string SourcePath => "";
        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 0, 0, 0, 0, 0));
        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(new ModelSnapshot("stub", 1601, []));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ModelRefreshResult> RefreshAsync(
            ModelRefreshRequest request, IProgress<RefreshProgress>? progress,
            TextWriter? traceWriter, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public string GenerateRefreshScript(ModelRefreshRequest request) => "";

        public Task<RefreshPolicyApplyResult> ApplyRefreshPolicyAsync(
            RefreshPolicyApplyRequest request, CancellationToken cancellationToken)
        {
            ApplyCalled = true;
            if (throwOnApply is not null)
                throw throwOnApply;
            return Task.FromResult(new RefreshPolicyApplyResult(
                "stub-server", "MyModel", request.Table,
                request.EffectiveDate ?? new DateOnly(2024, 1, 1),
                request.Refresh, ["created partition '2024'"], DurationMs: 1));
        }
    }
}
