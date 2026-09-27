using Spectre.Console;

namespace Tomix.Cli.Output;

/// <summary>
/// The stderr console for commentary: banners, hints, progress notes and prompts. Stdout carries
/// only the result, so <c>tx validate &gt; findings.txt</c> captures findings and nothing else.
/// </summary>
internal static class StdErr
{
    /// <summary>
    /// Creates a console over <see cref="System.Console.Error"/>. Created per call because tests swap
    /// <c>Console.Error</c>, so a cached console would miss the swap. Inherits the stdout
    /// console's no-color setting (<c>NO_COLOR</c> and the <c>noColor</c> config).
    /// </summary>
    /// <remarks>
    /// Redirected stderr (<c>2&gt; log.txt</c>, a CI log, a test's writer) has no width, and Spectre
    /// would fall back to 80 columns and hard-wrap: a long model path in the "Connected to:" banner
    /// or an error message would be split mid-line, so it could not be copied or grepped. Only a
    /// terminal keeps its real width.
    /// </remarks>
    public static IAnsiConsole Console()
    {
        var settings = new AnsiConsoleSettings { Out = new AnsiConsoleOutput(System.Console.Error) };
        if (AnsiConsole.Profile.Capabilities.ColorSystem == ColorSystem.NoColors)
            settings.ColorSystem = ColorSystemSupport.NoColors;

        var console = AnsiConsole.Create(settings);
        if (!console.Profile.Out.IsTerminal)
            console.Profile.Width = int.MaxValue;
        return console;
    }

    /// <summary>
    /// Writes one markup line to stderr, unwrapped when stderr is redirected (see
    /// <see cref="Console"/>); a terminal still wraps at its own width.
    /// </summary>
    public static void MarkupLine(string markup) => Console().MarkupLine(markup);
}
