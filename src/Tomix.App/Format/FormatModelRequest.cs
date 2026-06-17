using Tomix.Core.Models;

namespace Tomix.App.Format;

public sealed record FormatModelRequest(
    ModelReference Model,
    string? Expression,
    string? Path,
    string Language,
    ModelObjectKind? Type,
    bool Long,
    bool Semicolons,
    bool NoSpaceAfterFunction,
    bool Save,
    string? SaveTo,
    string Serialization = "",
    bool Force = false,
    bool Stage = false,
    bool Revert = false);
