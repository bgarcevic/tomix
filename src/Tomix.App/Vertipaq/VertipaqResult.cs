using Tomix.Core.Vertipaq;

namespace Tomix.App.Vertipaq;

/// <summary>
/// <paramref name="AnalyzedSource"/> is the endpoint or <c>.vpax</c> path the statistics came
/// from; <paramref name="UsedRemoteFallback"/> is true when a workspace session's remote side
/// was analyzed because the primary resolved to a local model definition.
/// </summary>
public sealed record VertipaqResult(
    VertipaqModelStats Stats,
    string AnalyzedSource,
    bool UsedRemoteFallback,
    string? ExportedPath,
    string? ObfuscationDictionaryPath,
    VertipaqAnnotateResult? Annotate);

public sealed record VertipaqAnnotateResult(
    int AnnotatedObjects,
    int SkippedObjects,
    object Saved,
    bool Synced = false,
    string? SyncTarget = null,
    string? SyncWarning = null);
