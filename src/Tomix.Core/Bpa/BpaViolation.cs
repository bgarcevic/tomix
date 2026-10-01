using Tomix.Core.Models;
using Tomix.Core.Rules;

namespace Tomix.Core.Bpa;

public sealed record BpaViolation(
    string RuleId,
    string RuleName,
    string Category,
    RuleSeverity Severity,
    string ObjectType,
    string ObjectName,
    string ObjectPath,
    string? Description = null,
    bool CanFix = false,
    ModelObjectKind? ObjectKind = null);
