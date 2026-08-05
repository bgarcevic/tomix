using System.CommandLine;
using Spectre.Console;

namespace Tomix.Cli.Tests;

/// <summary>
/// Runs a command with <c>Console.Out</c>/<c>Console.Error</c> (and optionally
/// <see cref="AnsiConsole"/>) redirected into buffers, and always restores them.
/// </summary>
/// <remarks>
/// This replaces fourteen hand-rolled copies of the same redirect, two of which were identical
/// 26-line blocks in the same file. Each copy was a chance to omit the <c>finally</c> and leave the
/// process-global writers pointing at a disposed buffer, which makes every later test in the run
/// fail in a way that has nothing to do with what it asserts. Classes using this belong in
/// <see cref="ConsoleStateCollection"/>, since the writers are process-global.
/// </remarks>
internal static class ConsoleCapture
{
    /// <param name="ExitCode">What <paramref name="run"/> returned, or 0 for the void overload.</param>
    internal readonly record struct Captured(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    /// Invokes <paramref name="run"/> with the console redirected.
    /// </summary>
    /// <param name="captureAnsiConsole">
    /// Also point <see cref="AnsiConsole.Console"/> at the stdout buffer. Needed only for output
    /// rendered through Spectre (tables, help, panels); plain <c>Console.WriteLine</c> output is
    /// captured either way.
    /// </param>
    public static Captured Run(Func<int> run, bool captureAnsiConsole = false)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var originalAnsiConsole = AnsiConsole.Console;

        Console.SetOut(stdout);
        Console.SetError(stderr);
        if (captureAnsiConsole)
            AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(stdout) });

        try
        {
            var exitCode = run();
            return new Captured(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            AnsiConsole.Console = originalAnsiConsole;
        }
    }

    /// <summary>For code that writes to the console but returns nothing; <c>ExitCode</c> is 0.</summary>
    public static Captured Run(Action run, bool captureAnsiConsole = false)
        => Run(() => { run(); return 0; }, captureAnsiConsole);

    /// <summary>Invokes <paramref name="parsed"/> with the console redirected.</summary>
    public static Captured Invoke(ParseResult parsed, bool captureAnsiConsole = false)
        => Run(() => parsed.Invoke(), captureAnsiConsole);

    /// <summary>
    /// Invokes <paramref name="parsed"/> through <see cref="Program.Invoke"/> — the path that adds
    /// the top-level exception-to-diagnostic mapping — with the console redirected.
    /// </summary>
    public static Captured InvokeThroughProgram(ParseResult parsed, bool captureAnsiConsole = false)
        => Run(() => Program.Invoke(parsed), captureAnsiConsole);
}
