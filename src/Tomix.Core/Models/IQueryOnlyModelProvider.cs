namespace Tomix.Core.Models;

/// <summary>
/// Provider capability that queries a model whose metadata the caller cannot read: read (Build)
/// permission only, where opening a full session fails with
/// <see cref="ModelConnectionFailureKind.MetadataUnavailable"/>. Only DAX and DMV queries work there.
/// </summary>
public interface IQueryOnlyModelProvider
{
    IModelQuerySession OpenQueryOnly(ModelReference reference);
}
