namespace Tomix.App.Config;

public sealed record ConfigInitResult(string Path, bool Created);

public sealed record ConfigPathsResult(string ConfigDir, string ConfigFile);

public sealed record ConfigListResult(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<string> UnsupportedKeys);

public sealed record ConfigGetResult(string Key, string? Value);

public sealed record ConfigSetResult(string Key, string Value);
