namespace Tomix.Core.Models;

public interface IModelExportSession
{
    Task<ModelExportResult> ExportAsync(
        ModelExportRequest request,
        CancellationToken cancellationToken);
}

public sealed record ModelExportRequest(
    string OutputPath,
    string Serialization,
    bool Force,
    bool SupportingFiles);

public sealed record ModelExportResult(
    string SavedPath,
    string Format);
