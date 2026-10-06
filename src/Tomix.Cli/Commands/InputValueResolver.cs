namespace Tomix.Cli.Commands;

/// <summary>
/// Resolves a command input value, honouring the <c>-</c> stdin sentinel and an optional
/// <c>--file</c> source. Shared by the <c>add</c>, <c>set</c>, <c>format</c>, and <c>query</c>
/// commands so the sentinel semantics stay identical across them.
/// </summary>
/// <remarks>
/// Stdin is read without <c>-</c> only when the caller says the value is the command's only
/// possible input (<c>readPipedInput</c>). A redirected stdin is not proof that anything was
/// piped: a CI step, an ssh session, or a parent process can hand tx a pipe that never closes,
/// and reading it to the end would block the command forever.
/// </remarks>
internal static class InputValueResolver
{
    /// <summary>
    /// Stands in for stdin per async flow: a test's input, which cannot leak into another test
    /// running in parallel, or the caller's stdin for a command run in a live session
    /// (<c>command.run</c>). A set value also counts as redirected input.
    /// </summary>
    internal static readonly AsyncLocal<TextReader?> Stdin = new();

    /// <summary>
    /// Reads from stdin when <paramref name="value"/> is <c>-</c>, or, with
    /// <paramref name="readPipedInput"/>, when it is <c>null</c>/<c>empty</c> and stdin is
    /// redirected (implicit piping). Otherwise returns it verbatim.
    /// </summary>
    public static string? Resolve(string? value, bool readPipedInput = false)
        => value == "-"
            ? ReadStdin()
            : readPipedInput && string.IsNullOrEmpty(value) && IsInputRedirected
                ? ReadStdin()
                : value;

    /// <summary>Reads from <paramref name="file"/> when supplied, otherwise falls back to <see cref="Resolve(string?, bool)"/>.</summary>
    public static string? Resolve(string? value, string? file, bool readPipedInput = false)
        => !string.IsNullOrWhiteSpace(file) ? File.ReadAllText(file) : Resolve(value, readPipedInput);

    private static bool IsInputRedirected => Stdin.Value is not null || Console.IsInputRedirected;

    // echo/heredoc pipes always end with a newline the user did not intend as part of the
    // value; keep interior newlines (multiline DAX/M) but drop the trailing ones. Windows
    // PowerShell 5.1 prefixes piped text with a BOM, which would otherwise land in the value.
    private static string ReadStdin()
        => (Stdin.Value ?? Console.In).ReadToEnd().TrimStart('﻿').TrimEnd('\r', '\n');
}
