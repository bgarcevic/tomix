using Tomix.App.Vertipaq;
using Tomix.Core.Authentication;
using Tomix.Core.Models;
using Tomix.Core.Vertipaq;

namespace Tomix.App.Tests;

public sealed class VertipaqHandlerTests
{
    private static readonly VertipaqModelStats Stats = NewStats();

    [Theory]
    [InlineData("in.vpax", "out.vpax", false, false, false)] // --import + --export
    [InlineData("in.vpax", null, false, true, false)]        // --import + --annotate
    [InlineData(null, null, true, false, false)]             // --obfuscate without --export
    [InlineData(null, null, false, false, true)]             // --save without --annotate
    public async Task HandleAsync_RejectsConflictingOptions(
        string? import, string? export, bool obfuscate, bool annotate, bool save)
    {
        var handler = new VertipaqHandler([], new StubAnalyzer());
        var result = await handler.HandleAsync(
            NewRequest(remote: true, import: import, export: export,
                obfuscate: obfuscate, annotate: annotate, save: save),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_VERTIPAQ_OPTIONS_CONFLICT", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_Import_ReadsTheVpaxFile()
    {
        var analyzer = new StubAnalyzer();
        var handler = new VertipaqHandler([], analyzer);

        var result = await handler.HandleAsync(
            NewRequest(remote: false, import: "stats.vpax"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("stats.vpax", analyzer.ImportedPath);
        Assert.Equal("stats.vpax", result.Data!.AnalyzedSource);
        Assert.Null(analyzer.AnalyzedModel);
    }

    [Fact]
    public async Task HandleAsync_Import_MapsReadFailures()
    {
        var analyzer = new StubAnalyzer
        {
            ImportError = new VertipaqAnalysisException(VertipaqAnalysisKind.VpaxReadFailed, "bad file")
        };
        var handler = new VertipaqHandler([], analyzer);

        var result = await handler.HandleAsync(
            NewRequest(remote: false, import: "stats.vpax"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_VPAX_READ_FAILED", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_Fails_WhenNoModelAndNoImport()
    {
        var handler = new VertipaqHandler([], new StubAnalyzer());
        var result = await handler.HandleAsync(
            new VertipaqRequest(new ModelReference(""), null, null, null, null, false, false, false),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_NO_MODEL", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_RejectsLocalSource_WithoutRemoteSide()
    {
        var handler = new VertipaqHandler([], new StubAnalyzer());
        var result = await handler.HandleAsync(
            NewRequest(remote: false),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_VERTIPAQ_UNSUPPORTED_SOURCE", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--import", result.Diagnostics[0].Hint);
    }

    [Fact]
    public async Task HandleAsync_AnalyzesRemotePrimary()
    {
        var analyzer = new StubAnalyzer();
        var handler = new VertipaqHandler([], analyzer);

        var result = await handler.HandleAsync(
            NewRequest(remote: true),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/ws", analyzer.AnalyzedModel!.Value);
        Assert.False(result.Data!.UsedRemoteFallback);
    }

    [Fact]
    public async Task HandleAsync_FallsBackToWorkspaceRemoteSide_WhenPrimaryIsLocal()
    {
        // connect --workspace with a local primary: reads must hit the live mirror endpoint.
        var analyzer = new StubAnalyzer();
        var handler = new VertipaqHandler([], analyzer);
        var remote = new ModelReference("powerbi://api.powerbi.com/v1.0/myorg/ws", "SalesModel");

        var result = await handler.HandleAsync(
            NewRequest(remote: false) with { RemoteSyncTarget = remote },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(remote, analyzer.AnalyzedModel);
        Assert.True(result.Data!.UsedRemoteFallback);
        Assert.Equal(remote.Value, result.Data.AnalyzedSource);
    }

    [Fact]
    public async Task HandleAsync_Export_ReturnsPathsAndStats()
    {
        var analyzer = new StubAnalyzer { DictionaryPath = "stats.dict" };
        var handler = new VertipaqHandler([], analyzer);

        var result = await handler.HandleAsync(
            NewRequest(remote: true, export: "stats.vpax", obfuscate: true),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("stats.vpax", result.Data!.ExportedPath);
        Assert.Equal("stats.dict", result.Data.ObfuscationDictionaryPath);
        Assert.True(analyzer.ExportObfuscate);
    }

    [Fact]
    public async Task HandleAsync_MapsAuthenticationRequired()
    {
        var analyzer = new StubAnalyzer { AnalyzeError = new AuthenticationRequiredException("login") };
        var handler = new VertipaqHandler([], analyzer);

        var result = await handler.HandleAsync(NewRequest(remote: true), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_AUTH_REQUIRED", result.Diagnostics[0].Code);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_MapsExtractionFailure()
    {
        var analyzer = new StubAnalyzer
        {
            AnalyzeError = new VertipaqAnalysisException(VertipaqAnalysisKind.ExtractionFailed, "dmv failed")
        };
        var handler = new VertipaqHandler([], analyzer);

        var result = await handler.HandleAsync(NewRequest(remote: true), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_VERTIPAQ_FAILED", result.Diagnostics[0].Code);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_MapsExportWriteFailure()
    {
        var analyzer = new StubAnalyzer
        {
            ExportError = new VertipaqAnalysisException(VertipaqAnalysisKind.VpaxWriteFailed, "disk full")
        };
        var handler = new VertipaqHandler([], analyzer);

        var result = await handler.HandleAsync(
            NewRequest(remote: true, export: "stats.vpax"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_VPAX_WRITE_FAILED", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task HandleAsync_TableFilter_SubsetsEverySection()
    {
        var handler = new VertipaqHandler([], new StubAnalyzer());

        var result = await handler.HandleAsync(
            NewRequest(remote: true) with { TableFilter = "sales" },
            CancellationToken.None);

        Assert.True(result.Success);
        var stats = result.Data!.Stats;
        Assert.Equal("Sales", Assert.Single(stats.Tables).TableName);
        Assert.All(stats.Columns, c => Assert.Equal("Sales", c.TableName));
        Assert.All(stats.Partitions, p => Assert.Equal("Sales", p.TableName));
        Assert.Single(stats.Relationships); // kept: Sales is one endpoint
    }

    [Fact]
    public async Task HandleAsync_TableFilter_FailsWithHint_WhenUnknown()
    {
        var handler = new VertipaqHandler([], new StubAnalyzer());

        var result = await handler.HandleAsync(
            NewRequest(remote: true) with { TableFilter = "Nope" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_VERTIPAQ_TABLE_NOT_FOUND", result.Diagnostics[0].Code);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Sales", result.Diagnostics[0].Hint);
    }

    [Fact]
    public async Task HandleAsync_Annotate_WritesVertipaqAnnotations_AndSkipsMissingObjects()
    {
        var mutator = new RecordingMutationSession(notFoundPaths: ["'Product'"]);
        var handler = new VertipaqHandler(
            [new StubMutationProvider(mutator)], new StubAnalyzer());

        var result = await handler.HandleAsync(
            NewRequest(remote: true, annotate: true),
            CancellationToken.None);

        Assert.True(result.Success);
        var annotate = result.Data!.Annotate;
        Assert.NotNull(annotate);
        Assert.Equal(1, annotate.SkippedObjects); // the Product table path was unresolvable

        // Model root, Sales table, columns, and the relationship endpoints, minus skips.
        var paths = mutator.SetRequests.Select(r => r.Path).ToList();
        Assert.Contains(".", paths);
        Assert.Contains("'Sales'", paths);
        Assert.Contains("'Sales'/'Amount'", paths);
        Assert.Contains("'Sales'[ProductKey]->'Product'[ProductKey]", paths);
        Assert.DoesNotContain(paths, p => p.Contains("RowNumber"));

        var salesAssignments = mutator.SetRequests.Single(r => r.Path == "'Sales'").Properties;
        Assert.Contains(salesAssignments, a => a is { Property: "Annotation:Vertipaq_RowCount", Value: "1000" });
    }

    [Fact]
    public async Task HandleAsync_Annotate_ReportsUnsavedOutcome_WithoutSave()
    {
        var mutator = new RecordingMutationSession(notFoundPaths: []);
        var handler = new VertipaqHandler(
            [new StubMutationProvider(mutator)], new StubAnalyzer());

        var result = await handler.HandleAsync(
            NewRequest(remote: true, annotate: true),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(false, result.Data!.Annotate!.Saved);
        Assert.False(mutator.Saved);
    }

    private static VertipaqRequest NewRequest(
        bool remote,
        string? import = null,
        string? export = null,
        bool obfuscate = false,
        bool annotate = false,
        bool save = false)
        => new(
            new ModelReference(remote ? "powerbi://api.powerbi.com/v1.0/myorg/ws" : "/models/sales", remote ? "SalesModel" : null),
            RemoteSyncTarget: null,
            TableFilter: null,
            ImportPath: import,
            ExportPath: export,
            Obfuscate: obfuscate,
            Annotate: annotate,
            Save: save);

    private static VertipaqModelStats NewStats()
    {
        var tables = new[]
        {
            new VertipaqTableStats("Sales", 1000, 1300, 1300, 800, 500, 0, 24, 0, 89.4, 2, 1, 2, true),
            new VertipaqTableStats("Product", 100, 130, 130, 80, 50, 0, 0, 0, 10.6, 1, 1, 1, true)
        };
        var columns = new[]
        {
            NewColumn("Sales", "Amount", 1000),
            NewColumn("Sales", "ProductKey", 300),
            NewColumn("Sales", "RowNumber-2662979B", 0, isRowNumber: true),
            NewColumn("Product", "ProductKey", 130)
        };
        var relationships = new[]
        {
            new VertipaqRelationshipStats(
                "'Sales'[ProductKey] -> 'Product'[ProductKey]",
                "Sales", "Product", "'Sales'[ProductKey]", "'Product'[ProductKey]",
                24, 100, 100, 0, 0, 0.1, true, "OneDirection")
        };
        var partitions = new[]
        {
            new VertipaqPartitionStats("Sales", "Sales-Part0", 1000, 800, 1, "Read", "M", "Import", null),
            new VertipaqPartitionStats("Product", "Product-Part0", 100, 80, 1, "Read", "M", "Import", null)
        };

        return new VertipaqModelStats(
            "SalesModel", "server", new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero),
            TotalSize: 1430, TableCount: 2, ColumnCount: 4, MaxRowCount: 1000,
            tables, columns, relationships, partitions);
    }

    private static VertipaqColumnStats NewColumn(
        string table, string name, long size, bool isRowNumber = false)
        => new(
            table, name, 100, "Int64", "HASH", size, size / 2, size / 2, 0,
            10, 20, 0.1, 1, 1, false, true, isRowNumber, "Ready");

    private sealed class StubAnalyzer : IVertipaqAnalyzer
    {
        public ModelReference? AnalyzedModel { get; private set; }
        public string? ImportedPath { get; private set; }
        public bool ExportObfuscate { get; private set; }
        public string? DictionaryPath { get; init; }
        public Exception? AnalyzeError { get; init; }
        public Exception? ImportError { get; init; }
        public Exception? ExportError { get; init; }

        public Task<VertipaqModelStats> AnalyzeAsync(ModelReference model, CancellationToken _)
        {
            AnalyzedModel = model;
            return AnalyzeError is null ? Task.FromResult(Stats) : Task.FromException<VertipaqModelStats>(AnalyzeError);
        }

        public Task<VertipaqModelStats> ImportAsync(string vpaxPath, CancellationToken _)
        {
            ImportedPath = vpaxPath;
            return ImportError is null ? Task.FromResult(Stats) : Task.FromException<VertipaqModelStats>(ImportError);
        }

        public Task<VertipaqExportResult> ExportAsync(
            ModelReference model, string vpaxPath, bool obfuscate, CancellationToken _)
        {
            AnalyzedModel = model;
            ExportObfuscate = obfuscate;
            return ExportError is null
                ? Task.FromResult(new VertipaqExportResult(Stats, vpaxPath, obfuscate ? DictionaryPath : null))
                : Task.FromException<VertipaqExportResult>(ExportError);
        }
    }

    private sealed class StubMutationProvider(RecordingMutationSession session) : IModelProvider
    {
        public bool CanOpen(ModelReference _) => true;

        public Task<IModelSession> OpenAsync(ModelReference _, CancellationToken __)
            => Task.FromResult<IModelSession>(session);
    }

    private sealed class RecordingMutationSession(IReadOnlyList<string> notFoundPaths)
        : IModelSession, IModelMutationSession
    {
        public List<ModelObjectSetRequest> SetRequests { get; } = [];
        public bool Saved { get; private set; }

        public string SourcePath => "";

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 0, 0, 0, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(new ModelSnapshot("stub", 1601, []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ModelObjectMutationResult AddObject(ModelObjectAddRequest request)
            => throw new NotSupportedException();

        public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request)
        {
            if (notFoundPaths.Contains(request.Path))
                throw new ObjectNotFoundException($"Object not found: {request.Path}");

            SetRequests.Add(request);
            return new ModelObjectMutationResult(request.Path, Changed: true);
        }

        public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request)
            => throw new NotSupportedException();

        public ModelReplaceResult ReplaceText(ModelReplaceRequest request)
            => throw new NotSupportedException();

        public Task<ModelExportResult> SaveAsync(
            string? outputPath, string serialization, bool force, CancellationToken _)
        {
            Saved = true;
            return Task.FromResult(new ModelExportResult(outputPath ?? "remote", "tmdl"));
        }
    }
}
