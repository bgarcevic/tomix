namespace Tomix.App.Format;

public sealed record ExpressionFormatResponse(
    bool Success,
    string Formatted,
    IReadOnlyList<string> Errors);
