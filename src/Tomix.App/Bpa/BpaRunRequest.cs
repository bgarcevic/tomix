using Tomix.Core.Models;

namespace Tomix.App.Bpa;

public sealed record BpaRunRequest(
    ModelReference Model,
    IReadOnlyList<string>? RulesFiles = null,
    bool NoDefaults = false,
    string? PathFilter = null,
    IReadOnlyList<string>? RuleIds = null,
    bool Fix = false,
    string? Ruleset = null,
    string? FailOn = null,
    bool Save = false,
    string? SaveTo = null,
    string Serialization = "",
    bool Force = false,
    bool NoModelRules = false,
    bool AllowExternalRules = false,
    bool Stage = false,
    bool Revert = false,
    bool NoSync = false);
