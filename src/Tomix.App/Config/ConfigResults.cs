namespace Tomix.App.Config;

public sealed record ConfigListResult(IReadOnlyDictionary<string, string> Values);

public sealed record ConfigGetResult(string Key, string? Value);

public sealed record ConfigSetResult(string Key, string Value);
