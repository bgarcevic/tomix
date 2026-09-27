namespace Tomix.Core.Diagnostics;

/// <summary>
/// A lexer or parser error in an M or DAX expression, language-neutral so every
/// expression engine reports positions the same way.
/// </summary>
/// <param name="Stage"><c>lex</c> or <c>parse</c>.</param>
/// <param name="Code">Stable camelCase name of the engine's error (for example <c>expectedTokenKind</c>), or null when the engine did not classify it.</param>
/// <param name="Message">The engine's message.</param>
/// <param name="Line">1-based start line, or null when the error has no position.</param>
/// <param name="Column">1-based start column, or null when the error has no position.</param>
/// <param name="EndLine">1-based line of the offending token's last character, or null when only the start is known.</param>
/// <param name="EndColumn">1-based column of the offending token's last character (inclusive), or null when only the start is known.</param>
public sealed record ExpressionSyntaxError(
    string Stage,
    string? Code,
    string Message,
    int? Line,
    int? Column,
    int? EndLine = null,
    int? EndColumn = null);
