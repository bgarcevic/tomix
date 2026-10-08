namespace Tomix.Cli.Output;

/// <summary>
/// The symbols tomix writes into its own text (not model data): Unicode in a terminal, ASCII
/// when stdout is redirected. Renderers take the current set from <see cref="Styling.Glyphs"/>;
/// Spectre-free views take it as a parameter.
/// </summary>
/// <param name="Separator">Between items on one line, with its spaces (<c> · </c>).</param>
/// <param name="Dash">Between a statement and its detail, with its spaces (<c> — </c>).</param>
/// <param name="Arrow">From a before value to an after value, with its spaces (<c> → </c>).</param>
internal sealed record Glyphs(
    string Bullet, string Times, string Separator, string Pass, string Fail, string Dash, string Arrow, string Ellipsis)
{
    public static readonly Glyphs Unicode = new("●", "×", " · ", "✓", "✗", " — ", " → ", "…");

    public static readonly Glyphs Ascii = new("*", "x", " - ", "OK", "FAIL", " - ", " -> ", "...");
}
