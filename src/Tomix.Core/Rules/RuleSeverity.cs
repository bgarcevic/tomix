namespace Tomix.Core.Rules;

/// <summary>
/// How serious a rule's finding is, shared by every rule engine (<c>validate</c>, <c>bpa</c>).
/// The numeric values are public surface: BPA rule files store severity as <c>1</c>–<c>3</c> and
/// <c>bpa</c> JSON output writes it as an integer, while <c>validate</c> JSON writes the name.
/// </summary>
public enum RuleSeverity
{
    Info = 1,
    Warning = 2,
    Error = 3
}
