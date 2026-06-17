using Tomix.Core.Models;

namespace Tomix.Core.Bpa;

public sealed record BpaViolation(
    string RuleId,
    string RuleName,
    string Category,
    BpaSeverity Severity,
    string ObjectType,
    string ObjectName,
    string ObjectPath,
    string? Description = null,
    bool CanFix = false,
    ModelObjectKind? ObjectKind = null);
