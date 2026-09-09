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
    bool Stage = false,
    bool Revert = false,
    bool NoSync = false,
    bool StrictRefs = false,
    bool FixRefs = true,
    bool Overwrite = false,
    bool DryRun = false);
