using Tomix.Core.Models;

namespace Tomix.App.Save;

public sealed record SaveModelRequest(
    ModelReference Model,
    string? OutputPath,
    string Serialization,
    bool Force,
    bool SupportingFiles,
    bool FixBpa = false,
    string[]? BpaRules = null,
    ModelReference? SyncTarget = null);
