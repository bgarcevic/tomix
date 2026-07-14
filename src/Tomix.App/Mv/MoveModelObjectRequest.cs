using Tomix.Core.Models;

namespace Tomix.App.Mv;

public sealed record MoveModelObjectRequest(
    ModelReference Model,
    string Source,
    string Destination,
    ModelObjectKind? Type,
    bool Save,
    string? SaveTo,
    string Serialization,
    bool Force,
    bool Stage = false,
    bool Revert = false,
    bool NoSync = false,
    bool StrictRefs = false,
    bool FixRefs = true);
