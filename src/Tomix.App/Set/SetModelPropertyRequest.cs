using Tomix.Core.Models;

namespace Tomix.App.Set;

public sealed record SetModelPropertyRequest(
    ModelReference Model,
    string Path,
    IReadOnlyList<ModelPropertyAssignment> Properties,
    ModelObjectKind? Type,
    bool Save,
    string? SaveTo,
    string Serialization,
    bool Force,
    bool Stage = false,
    bool Revert = false,
    bool NoSync = false);
