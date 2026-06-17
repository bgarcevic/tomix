namespace Tomix.App.Save;

public sealed record SaveModelResult(
    string Saved,
    string Format,
    bool Synced = false,
    string? SyncTarget = null,
    string? SyncWarning = null);
