using Tomix.Core.Models;

namespace Tomix.App.Get;

public sealed record GetModelRequest(
    ModelReference Model,
    string Path,
    string? Query,
    ModelObjectKind? Type);
