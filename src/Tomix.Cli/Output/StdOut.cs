using Spectre.Console;

namespace Tomix.Cli.Output;

/// <summary>
/// The stdout console's policy for redirected output, applied once at startup.
/// </summary>
internal static class StdOut
{
    /// <summary>
    /// Makes a redirected stdout plain: unwrapped, with ASCII table borders and tree guides. A
    /// terminal keeps its own width and the rounded Unicode borders.
    /// </summary>
    /// <remarks>
    /// Redirected stdout (a pipe, a file, an agent's tool call) has no width, and Spectre would
    /// fall back to 80 columns: <c>tx ls</c> tables then wrap each cell, so a DAX expression or a
    /// format string is split across several table rows and cannot be grepped or copied back into
    /// <c>tx set</c>. Unbounded, every row is one line. Renderers that lay out prose cap their own
    /// width (see <c>BpaRunRenderer</c>).
    /// <para>
    /// Box-drawing borders are also dropped: a pipe through an OEM-code-page consumer (Windows
    /// PowerShell 5.1, <c>more</c>, older CI logs) turns them into mojibake, and each glyph costs
    /// an agent more tokens than <c>|</c> or <c>-</c>. Spectre swaps in each border's ASCII-safe
    /// form when the console reports no Unicode support.
    /// </para>
    /// </remarks>
    public static void PlainWhenRedirected(IAnsiConsole console)
    {
        if (console.Profile.Out.IsTerminal)
            return;

        console.Profile.Width = int.MaxValue;
        console.Profile.Capabilities.Unicode = false;
    }
}
