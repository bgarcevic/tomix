using Tomix.Core.Models;

namespace Tomix.App.Deploy;

public sealed record DeployModelRequest(
    ModelReference Model,
    string? Server,
    string? Database,
    string? Profile,
    bool CreateOnly,
    bool SkipBpa,
    bool FixBpa,
    string[]? BpaRules,
    string? XmlaOutput,
    string? Ci,
    bool Preview = false,
    ModelDeployOptions? DeployOptions = null,
    string? BpaFailOn = null);
