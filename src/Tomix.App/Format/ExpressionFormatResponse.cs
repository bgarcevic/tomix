using Tomix.Core.Diagnostics;

namespace Tomix.App.Format;

/// <param name="Errors">Human-readable failure messages; empty on success.</param>
public sealed record ExpressionFormatResponse(
    bool Success,
    string Formatted,
    IReadOnlyList<string> Errors)
{
    /// <summary>
    /// Structured lexer/parser errors when the expression does not parse; empty on success and for
    /// failures without a position (an engine timeout, or DAX the formatter declines).
    /// </summary>
    public IReadOnlyList<ExpressionSyntaxError> SyntaxErrors { get; init; } = [];
}
