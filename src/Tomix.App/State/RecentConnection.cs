namespace Tomix.App.State;

public sealed record RecentConnection(
    CliConnectionState Connection,
    DateTimeOffset LastUsed);
