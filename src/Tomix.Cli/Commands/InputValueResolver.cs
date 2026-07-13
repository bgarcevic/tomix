namespace Tomix.Cli.Commands;

/// <summary>
/// Resolves a command input value, honouring the <c>-</c> stdin sentinel and an optional
/// <c>--file</c> source. Shared by the <c>add</c>, <c>set</c>, <c>format</c>, and <c>script</c>
/// commands so the sentinel semantics stay identical across them.
/// </summary>
internal static class InputValueResolver
{
    /// <summary>
    /// Reads from stdin when <paramref name="value"/> is <c>-</c>, or when it is
    /// <c>null</c>/<c>empty</c> and stdin is redirected (implicit piping). Otherwise returns it verbatim.
    /// </summary>
    public static string? Resolve(string? value)
        => value == "-"
            ? ReadStdin()
            : string.IsNullOrEmpty(value) && Console.IsInputRedirected
                ? ReadStdin()
                : value;

    // echo/heredoc pipes always end with a newline the user did not intend as part of the
    // value; keep interior newlines (multiline DAX/M) but drop the trailing ones.
    private static string ReadStdin()
        => Console.In.ReadToEnd().TrimEnd('\r', '\n');

    /// <summary>Reads from <paramref name="file"/> when supplied, otherwise falls back to <see cref="Resolve(string?)"/>.</summary>
    public static string? Resolve(string? value, string? file)
        => !string.IsNullOrWhiteSpace(file) ? File.ReadAllText(file) : Resolve(value);
}
