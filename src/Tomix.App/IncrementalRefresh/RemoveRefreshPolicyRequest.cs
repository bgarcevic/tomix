using Tomix.Core.Models;

namespace Tomix.App.IncrementalRefresh;

public sealed record RemoveRefreshPolicyRequest(
    ModelReference Model,
    string Table,
    bool IfExists,
    bool Save,
    string? SaveTo,
    string Serialization,
    bool Force,
    bool Stage = false,
    bool Revert = false,
    bool NoSync = false);
