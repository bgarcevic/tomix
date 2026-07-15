using Tomix.Core.Models;

namespace Tomix.App.Vertipaq;

/// <summary>
/// View/field/top selection is presentation-only and stays in the CLI; the handler always
/// returns full statistics. <paramref name="RemoteSyncTarget"/> is the remote side of a
/// workspace session (from <c>ActiveModelResolver.ResolveSyncTarget</c>): when
/// <paramref name="Model"/> resolves to a local path, statistics are read from there instead,
/// while <c>--annotate</c> keeps mutating <paramref name="Model"/> so workspace save-mirroring
/// applies as usual.
/// </summary>
public sealed record VertipaqRequest(
    ModelReference Model,
    ModelReference? RemoteSyncTarget,
    string? TableFilter,
    string? ImportPath,
    string? ExportPath,
    bool Obfuscate,
    bool Annotate,
    bool Save);
