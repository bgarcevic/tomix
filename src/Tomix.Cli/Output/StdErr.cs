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
    public static IAnsiConsole Console()
    {
        var settings = new AnsiConsoleSettings { Out = new AnsiConsoleOutput(System.Console.Error) };
        if (AnsiConsole.Profile.Capabilities.ColorSystem == ColorSystem.NoColors)
            settings.ColorSystem = ColorSystemSupport.NoColors;
        return AnsiConsole.Create(settings);
    }

    /// <summary>
    /// Writes one markup line to stderr. When stderr is not a terminal (redirected to a file or
    /// pipe), Spectre falls back to an 80-column width and hard-wraps at word boundaries, which
    /// splits long values such as model paths across lines in <c>2&gt; log.txt</c>. A line of
    /// commentary is written unwrapped there; a terminal still wraps at its own width.
    /// </summary>
    public static void MarkupLine(string markup)
    {
        var console = Console();
        if (!console.Profile.Out.IsTerminal)
            console.Profile.Width = int.MaxValue;
        console.MarkupLine(markup);
    }
}
