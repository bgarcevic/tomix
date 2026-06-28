using Tomix.Core.Models;

namespace Tomix.App.Rm;

public sealed record RemoveModelObjectRequest(
    ModelReference Model,
    string Path,
    ModelObjectKind? Type,
    bool IfExists,
    bool DryRun,
    bool Save,
    string? SaveTo,
    string Serialization,
    bool Force,
    bool Stage = false,
    bool Revert = false,
    bool NoSync = false);
