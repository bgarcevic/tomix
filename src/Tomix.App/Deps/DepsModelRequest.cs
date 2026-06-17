using Tomix.Core.Models;

namespace Tomix.App.Deps;

public sealed record DepsModelRequest(
    ModelReference Model,
    string? Path,
    ModelObjectKind? Type,
    bool UpstreamOnly,
    bool DownstreamOnly,
    bool Deep,
    bool Unused,
    bool HiddenOnly,
    int MaxDepth);
