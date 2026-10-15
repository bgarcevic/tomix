using Tomix.Core.Models;

namespace Tomix.App.Rm;

/// <summary>Remove several objects as one mutation (see <see cref="RemoveBatchHandler"/>).
/// <paramref name="Cascade"/> allows structural cascades onto objects outside the batch.</summary>
public sealed record RemoveBatchRequest(
    ModelReference Model,
    IReadOnlyList<RemoveTarget> Targets,
    bool IfExists,
    bool Save,
    string? SaveTo,
    string Serialization,
    bool Force,
    bool Cascade,
    bool Stage = false,
    bool Revert = false,
    bool NoSync = false,
    bool Overwrite = false);
