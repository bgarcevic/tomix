namespace Tomix.Core.Models;

public interface IModelMutationSession
{
    ModelObjectMutationResult AddObject(ModelObjectAddRequest request);

    ModelObjectMutationResult SetProperty(ModelObjectSetRequest request);

    ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request);

    ModelReplaceResult ReplaceText(ModelReplaceRequest request);

    Task<ModelExportResult> SaveAsync(
        string? outputPath,
        string serialization,
        bool force,
        CancellationToken cancellationToken);
}

public sealed record ModelObjectAddRequest(
    string Path,
    string? Type,
    string? Value,
    IReadOnlyList<ModelPropertyAssignment> Properties,
    bool IfNotExists,
    string? Columns = null,
    string? Mode = null,
    string? Source = null,
    string? Endpoint = null,
    string? ConnectionString = null,
    string? SourceTable = null,
    string? SourceDatabase = null,
    string? PartitionExpression = null,
    string? SourceType = null);

public sealed record ModelObjectSetRequest(
    string Path,
    IReadOnlyList<ModelPropertyAssignment> Properties,
    ModelObjectKind? Type);

public sealed record ModelObjectRemoveRequest(
    string Path,
    ModelObjectKind? Type,
    bool IfExists);

public sealed record ModelReplaceRequest(
    string Pattern,
    string Replacement,
    string Scope,
    bool Regex,
    bool CaseSensitive,
    bool Apply);

public sealed record ModelPropertyAssignment(
    string Property,
    string Value);

public sealed record ModelObjectMutationResult(
    string Path,
    bool Changed,
    string? Property = null,
    string? Value = null,
    string? Reason = null);

public sealed record ModelReplaceResult(
    int ChangeCount,
    IReadOnlyList<ModelReplacePreview> Previews);

public sealed record ModelReplacePreview(
    string ObjectPath,
    string Property,
    string Before,
    string After);
