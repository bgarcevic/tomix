namespace Tomix.App.Init;

public sealed record InitModelResult(
    string Created,
    string Format,
    string Name,
    int CompatibilityLevel,
    string CompatibilityMode);
