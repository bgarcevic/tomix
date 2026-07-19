namespace Tomix.Cli.Output;

/// <summary>One CI logging command: an error/warning level plus a pre-formatted message.</summary>
internal readonly record struct CiAnnotation(bool IsError, string Message);

/// <summary>
/// Writes CI logging commands (<c>--ci github</c> / <c>--ci vsts</c>) to a plain-text writer.
/// Callers project their domain results into <see cref="CiAnnotation"/>s; this type owns the
/// annotation syntax only. Output must never contain Spectre markup or escaping.
/// </summary>
internal static class CiAnnotations
{
    /// <summary>
    /// Emits one logging command per annotation. <paramref name="ci"/> values other than
    /// <c>github</c>/<c>vsts</c> (including null/blank) are a no-op. For vsts, a
    /// <c>task.complete result=Failed</c> trailer follows when any annotation is an error.
    /// Messages may carry untrusted model data (cell values, server error text); line breaks
    /// are collapsed so a value cannot start a new line and be parsed as its own
    /// <c>::level::</c> / <c>##vso</c> logging command (both syntaxes are line-oriented).
    /// </summary>
    public static void Emit(string? ci, IReadOnlyList<CiAnnotation> annotations, TextWriter writer)
    {
        if (string.IsNullOrWhiteSpace(ci) || annotations.Count == 0)
            return;

        if (ci.Equals("github", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var annotation in annotations)
            {
                var level = annotation.IsError ? "error" : "warning";
                writer.WriteLine($"::{level}::{SingleLine(annotation.Message)}");
            }
        }
        else if (ci.Equals("vsts", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var annotation in annotations)
            {
                var type = annotation.IsError ? "error" : "warning";
                writer.WriteLine($"##vso[task.logissue type={type};]{SingleLine(annotation.Message)}");
            }

            if (annotations.Any(a => a.IsError))
                writer.WriteLine("##vso[task.complete result=Failed;]Done.");
        }
    }

    private static string SingleLine(string message)
        => message.ReplaceLineEndings(" ");
}
