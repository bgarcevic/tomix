namespace Tomix.App.Init;

public sealed record InitModelRequest(
    string OutputPath,
    string? Name,
    string Serialization,
    string CompatibilityMode,
    int? CompatibilityLevel,
    bool Force);
