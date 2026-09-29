namespace Tomix.Cli.Output;

/// <summary>One CI logging command: an error/warning level plus a pre-formatted message.</summary>
internal readonly record struct CiAnnotation(bool IsError, string Message);

/// <summary>
/// Writes CI logging commands (<c>--ci github</c> / <c>--ci vsts</c>) to a plain-text writer.
/// Callers project their domain results into <see cref="CiAnnotation"/>s; this type owns the
/// annotation syntax only. Output must never contain Spectre markup or escaping.
/// </summary>
internal static partial class CiAnnotations
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
                writer.WriteLine($"##vso[task.logissue type={type};]{EscapeAzureLogCommands(SingleLine(annotation.Message))}");
            }

            if (annotations.Any(a => a.IsError))
                writer.WriteLine("##vso[task.complete result=Failed;]Done.");
        }
    }

    /// <summary>
    /// Whether text output lands in a CI build log: <c>--ci</c> was given, or stdout is redirected
    /// under Azure Pipelines (<c>TF_BUILD</c>).
    /// </summary>
    public static bool IsCiLog(string? ci)
        => !string.IsNullOrWhiteSpace(ci) || (IsAzurePipelinesEnvironment() && Console.IsOutputRedirected);

    /// <summary>Whether output lands in an Azure Pipelines log: <c>--ci vsts</c> or <c>TF_BUILD</c>.</summary>
    public static bool IsAzurePipelinesLog(string? ci)
        => string.Equals(ci, "vsts", StringComparison.OrdinalIgnoreCase)
           || (IsAzurePipelinesEnvironment() && Console.IsOutputRedirected);

    private static bool IsAzurePipelinesEnvironment()
        => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD"));

    /// <summary>
    /// The Azure Pipelines log viewer treats a formatting command such as <c>[Group]</c> anywhere
    /// in a line as <c>##[group]</c>: the line becomes a collapsible section header, loses its
    /// <c>##[error]</c> styling, and table rows sprout escape-code fragments. A column called
    /// Group is common, so a zero-width space after the opening bracket keeps the text looking
    /// the same while no longer matching.
    /// </summary>
    public static string EscapeAzureLogCommands(string text)
        => AzureLogCommand().Replace(text, "[​$1]");

    [System.Text.RegularExpressions.GeneratedRegex(
        @"\[(group|endgroup|section|command|debug|warning|error)\]",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex AzureLogCommand();

    private static string SingleLine(string message)
        => message.ReplaceLineEndings(" ");
}
