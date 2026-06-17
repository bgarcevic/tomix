using Tomix.Core.Models;

namespace Tomix.App.Mutations;

/// <summary>
/// Shared persistence tail for mutation handlers: a save is requested when <c>--save</c> is set or a
/// <c>--save-to</c> path is supplied, in which case the mutation is flushed via
/// <see cref="IModelMutationSession.SaveAsync"/> and the saved path is returned.
/// </summary>
internal static class MutationSave
{
    /// <summary>True when the caller asked to persist (either <c>--save</c> or a <c>--save-to</c> path).</summary>
    public static bool Requested(bool save, string? saveTo)
        => save || !string.IsNullOrWhiteSpace(saveTo);

    /// <summary>Persists the pending mutation and returns the saved path (boxed for the result records' <c>object</c> field).</summary>
    public static async Task<object> RunAsync(
        IModelMutationSession mutator,
        string? saveTo,
        string serialization,
        bool force,
        CancellationToken cancellationToken)
    {
        var export = await mutator.SaveAsync(saveTo, serialization, force, cancellationToken);
        return export.SavedPath;
    }
}
