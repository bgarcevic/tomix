using Tomix.Core.Rules;

namespace Tomix.App.Validate;

/// <summary>
/// A built-in <c>validate</c> check described by the shared <see cref="RuleDefinition"/> shape.
/// The check itself is code in <see cref="ModelValidation"/> (no <see cref="RuleDefinition.Expression"/>);
/// the rule owns the issue code and severity, so an issue can only be raised through
/// <see cref="Issue"/> with the severity its rule declares.
/// </summary>
public sealed record ValidationRule(
    string Id,
    string Name,
    string Category,
    RuleSeverity Severity,
    IReadOnlyList<string> Scope,
    string? Description = null)
    : RuleDefinition(Id, Name, Category, Severity, Scope, Description, DocsUrl: ValidationRules.DocsUrl)
{
    /// <summary>A finding of this rule on <paramref name="objectName"/>.</summary>
    public ValidationIssue Issue(string message, string objectName, string? expression = null, string? expressionLine = null)
        => new(Severity, Id, message, objectName, expression, expressionLine);
}

/// <summary>
/// The catalog of built-in <c>validate</c> rules. Every issue <c>validate</c> reports comes from
/// one of these; <c>docs/error-codes.md</c> documents each (pinned by <c>ValidationRulesTests</c>).
/// </summary>
public static class ValidationRules
{
    /// <summary>Where the validate issue codes are documented.</summary>
    public const string DocsUrl = "https://bgarcevic.github.io/tomix-cli/error-codes/#validate-issue-codes";

    private const string DaxCategory = "DAX Expressions";
    private const string IntegrityCategory = "Model Integrity";

    /// <summary>Every object kind that carries DAX (see <c>DaxExpressions.Sites</c>).</summary>
    private static readonly IReadOnlyList<string> DaxScope =
        ["Table", "Measure", "CalculatedColumn", "CalculationItem", "CalculatedTable", "ModelRole", "Function"];

    public static readonly ValidationRule UnknownTable = new(
        "DAX0001", "Unknown table", DaxCategory, RuleSeverity.Error, DaxScope,
        "A DAX expression references a table that does not exist in the model.");

    public static readonly ValidationRule UnknownColumn = new(
        "DAX0002", "Unknown column", DaxCategory, RuleSeverity.Error, DaxScope,
        "A DAX expression references a column that does not exist on the named table.");

    public static readonly ValidationRule UnresolvedReference = new(
        "DAX0003", "Unresolved reference", DaxCategory, RuleSeverity.Warning, DaxScope,
        "An unqualified reference resolves to no measure or column, or to a column the expression builds itself used outside its table.");

    public static readonly ValidationRule InvalidSyntax = new(
        "DAX0004", "Invalid character or unbalanced group", DaxCategory, RuleSeverity.Error, DaxScope,
        "A DAX expression contains a character that starts no token, or an unbalanced parenthesis or brace.");

    public static readonly ValidationRule UnterminatedLiteral = new(
        "DAX0005", "Unterminated literal", DaxCategory, RuleSeverity.Error, DaxScope,
        "A DAX expression contains an unterminated string, table name, column reference, or block comment.");

    public static readonly ValidationRule SelfReference = new(
        "DAX0006", "Self reference", DaxCategory, RuleSeverity.Error, ["Measure", "CalculatedColumn"],
        "A measure or calculated column expression directly references itself.");

    public static readonly ValidationRule UnexpectedToken = new(
        "DAX0007", "Unexpected token", DaxCategory, RuleSeverity.Error, DaxScope,
        "A token appears where a comma, a closing bracket, or the end of the expression belongs.");

    public static readonly ValidationRule MissingOperand = new(
        "DAX0008", "Missing operand", DaxCategory, RuleSeverity.Error, DaxScope,
        "An operator, a closing bracket, or the end of the expression appears where an operand belongs.");

    public static readonly ValidationRule IncompleteVarBlock = new(
        "DAX0009", "Incomplete VAR block", DaxCategory, RuleSeverity.Error, DaxScope,
        "A VAR has no '=' after its name, or a VAR block has no RETURN.");

    public static readonly ValidationRule BrokenRelationship = new(
        "TOMIX_BROKEN_RELATIONSHIP", "Broken relationship", IntegrityCategory, RuleSeverity.Error, ["Relationship"],
        "A relationship endpoint refers to a missing column.");

    public static readonly ValidationRule BrokenSortBy = new(
        "TOMIX_BROKEN_SORT_BY", "Broken sort-by column", IntegrityCategory, RuleSeverity.Error, ["Column"],
        "A column's sort-by column does not exist on its table.");

    public static readonly ValidationRule BrokenLevel = new(
        "TOMIX_BROKEN_LEVEL", "Broken hierarchy level", IntegrityCategory, RuleSeverity.Error, ["Level"],
        "A hierarchy level is bound to a column that does not exist on its table.");

    public static readonly ValidationRule ModelLoadFailed = new(
        "TOMIX_MODEL_LOAD_FAILED", "Model load failed", IntegrityCategory, RuleSeverity.Error, ["Model"],
        "The model could not be opened or snapshotted at all.");

    /// <summary>Every built-in rule, in documentation order.</summary>
    public static IReadOnlyList<ValidationRule> All { get; } =
    [
        UnknownTable,
        UnknownColumn,
        UnresolvedReference,
        InvalidSyntax,
        UnterminatedLiteral,
        SelfReference,
        UnexpectedToken,
        MissingOperand,
        IncompleteVarBlock,
        BrokenRelationship,
        BrokenSortBy,
        BrokenLevel,
        ModelLoadFailed,
    ];
}
