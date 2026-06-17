using Tomix.Core.Models;

namespace Tomix.App.Diff;

public sealed record DiffModelRequest(
    ModelReference Left,
    ModelReference Right);
