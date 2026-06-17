using Tomix.Core.Models;

namespace Tomix.App.Ls;

/// <summary>
/// A request to list model objects. <paramref name="PathFilter"/> is the optional object-path
/// selector (null/empty lists tables); <paramref name="Type"/> optionally narrows to one kind.
/// </summary>
public sealed record LsModelRequest(
    ModelReference Model,
    string? PathFilter,
    ModelObjectKind? Type);
