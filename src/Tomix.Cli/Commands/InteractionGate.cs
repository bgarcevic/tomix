using System.CommandLine;
using Tomix.Cli.Output;

namespace Tomix.Cli.Commands;

/// <summary>
/// Single source of truth for whether the CLI may show an interactive prompt. A prompt is
/// only allowed when a human is at the keyboard reading a text-formatted session: not
/// <c>--non-interactive</c>, not <c>--quiet</c>, text output (never json/csv), and neither
/// stdin nor stderr redirected. Every interactive path has a flag equivalent that runs when
/// this returns false (see docs/cli-ux-guidelines.md — "Interactivity").
/// </summary>
internal static class InteractionGate
{
    public static bool CanPrompt(ParseResult parseResult, string outputFormat)
        => CanPrompt(
            parseResult.GetValue(GlobalOptions.NonInteractive),
            parseResult.GetValue(GlobalOptions.Quiet),
            outputFormat,
            Console.IsInputRedirected,
            Console.IsErrorRedirected);

    internal static bool CanPrompt(
        bool nonInteractive,
        bool quiet,
        string outputFormat,
        bool stdinRedirected,
        bool stderrRedirected)
        => !nonInteractive
           && !quiet
           && OutputFormats.IsTextLike(outputFormat)
           && !stdinRedirected
           && !stderrRedirected;
}
