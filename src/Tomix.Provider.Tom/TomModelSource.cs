using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Authentication;
using Tomix.Core.Models;
using TabularServer = Microsoft.AnalysisServices.Tabular.Server;

namespace Tomix.Provider.Tom;

/// <summary>
/// Where a live session's model comes from and goes back to: how to load it, name it, save and
/// export it, and how its checkpoints restore (ADR 0003 §1). Everything else a session does is
/// the same for every source.
/// </summary>
internal abstract class TomModelSource : IAsyncDisposable
{
    protected TomModelSource(ModelReference reference, IAccessTokenProvider? tokenProvider)
    {
        Reference = reference;
        TokenProvider = tokenProvider;
    }

    public ModelReference Reference { get; }

    public IAccessTokenProvider? TokenProvider { get; }

    public abstract string SourcePath { get; }

    public abstract TomCheckpointRestore Restore { get; }

    public abstract Database Load();

    public abstract string ModelName(Database database);

    public ModelSummary Summarize(Database database)
        => TomModelSummarizer.Summarize(database, ModelName(database))
            with
        { DatabaseName = string.IsNullOrWhiteSpace(database.Name) ? null : database.Name };

    public abstract Task<ModelExportResult> ExportAsync(Database database, ModelExportRequest request, CancellationToken cancellationToken);

    public abstract Task<ModelExportResult> SaveAsync(Database database, string? outputPath, string serialization, bool overwrite, CancellationToken cancellationToken);

    /// <summary>Whether a save to <paramref name="outputPath"/> writes the source itself, and so
    /// moves the session's save point.</summary>
    public abstract bool IsInPlace(string? outputPath);

    public abstract ValueTask DisposeAsync();

    /// <summary>A TMDL folder, <c>.bim</c> or TMSL file, saved in place in
    /// <paramref name="sourceFormat"/>.</summary>
    public static TomModelSource File(ModelReference reference, string path, string sourceFormat, Func<string, Database> load, IAccessTokenProvider? tokenProvider)
        => new FileSource(reference, path, sourceFormat, load, tokenProvider);

    private sealed class FileSource(ModelReference reference, string path, string sourceFormat, Func<string, Database> load, IAccessTokenProvider? tokenProvider)
        : TomModelSource(reference, tokenProvider)
    {
        public override string SourcePath => path;

        public override TomCheckpointRestore Restore => TomCheckpointRestore.Swap;

        public override Database Load() => load(path);

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public override bool IsInPlace(string? outputPath)
            => string.IsNullOrWhiteSpace(outputPath) || SamePath(outputPath, path);

        public override string ModelName(Database database) => ModelDisplayName.Resolve(database.Name, path);

        public override Task<ModelExportResult> ExportAsync(Database database, ModelExportRequest request, CancellationToken cancellationToken)
            => TomModelExporter.ExportAsync(
                database,
                SamePath(request.OutputPath, path) ? request with { Overwrite = true } : request,
                cancellationToken);

        public override Task<ModelExportResult> SaveAsync(Database database, string? outputPath, string serialization, bool overwrite, CancellationToken cancellationToken)
        {
            var inPlace = IsInPlace(outputPath);
            var format = InPlaceSerializationGuard.Resolve(inPlace, serialization, sourceFormat);
            return TomModelExporter.ExportAsync(
                database,
                new ModelExportRequest(
                    string.IsNullOrWhiteSpace(outputPath) ? path : outputPath,
                    format,
                    Overwrite: overwrite || inPlace,
                    SupportingFiles: false),
                cancellationToken);
        }

        private static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}

/// <summary>
/// A database on an XMLA server or in Power BI Desktop. The session owns the connection. The
/// <see cref="Database"/> belongs to its <see cref="TabularServer"/> and cannot be swapped, so
/// checkpoints restore with <c>CopyTo</c>.
/// </summary>
internal sealed class TomServerModelSource(TabularServer server, Database database, ModelReference reference, IAccessTokenProvider? tokenProvider)
    : TomModelSource(reference, tokenProvider)
{
    public TabularServer Server => server;

    public override string SourcePath => "";

    public override TomCheckpointRestore Restore => TomCheckpointRestore.CopyTo;

    public override Database Load() => database;

    /// <summary>A save always sends the edits to the server, whatever else it writes.</summary>
    public override bool IsInPlace(string? outputPath) => true;

    public override string ModelName(Database database)
        => ModelDisplayName.Resolve(database.Name, sourcePath: null, fallback: database.ID);

    public override Task<ModelExportResult> ExportAsync(Database database, ModelExportRequest request, CancellationToken cancellationToken)
        => TomModelExporter.ExportAsync(database, request, cancellationToken);

    public override Task<ModelExportResult> SaveAsync(Database database, string? outputPath, string serialization, bool overwrite, CancellationToken cancellationToken)
        => TomServerModelSession.SaveAsync(database, ModelName(database), outputPath, serialization, overwrite, cancellationToken);

    public override ValueTask DisposeAsync()
    {
        if (server.Connected)
            server.Disconnect();
        server.Dispose();
        return ValueTask.CompletedTask;
    }
}
