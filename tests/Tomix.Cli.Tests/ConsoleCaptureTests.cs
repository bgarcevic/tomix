using Spectre.Console;

namespace Tomix.Cli.Tests;

/// <summary>
/// <see cref="ConsoleCapture"/> now stands behind every console assertion in this project, so its
/// restore path is worth pinning directly. A capture that leaks a redirect makes later tests fail
/// for reasons unrelated to what they assert; one that never restores on the exception path is the
/// specific bug the hand-rolled copies were at risk of.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class ConsoleCaptureTests
{
    [Fact]
    public void Run_CapturesBothStreams_AndTheExitCode()
    {
        var captured = ConsoleCapture.Run(() =>
        {
            Console.Out.Write("to stdout");
            Console.Error.Write("to stderr");
            return 3;
        });

        Assert.Equal(3, captured.ExitCode);
        Assert.Equal("to stdout", captured.Stdout);
        Assert.Equal("to stderr", captured.Stderr);
    }

    [Fact]
    public void Run_RestoresTheWriters_WhenTheRunThrows()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var originalAnsiConsole = AnsiConsole.Console;

        Assert.Throws<InvalidOperationException>(() => ConsoleCapture.Run(
            () => throw new InvalidOperationException("boom"),
            captureAnsiConsole: true));

        Assert.Same(originalOut, Console.Out);
        Assert.Same(originalError, Console.Error);
        Assert.Same(originalAnsiConsole, AnsiConsole.Console);
    }

    [Fact]
    public void Run_LeavesAnsiConsoleAlone_WhenNotAskedToCaptureIt()
    {
        // Spectre-rendered output is only captured on request, so a test that asserts on plain
        // Console output cannot accidentally depend on the global AnsiConsole being swapped.
        var original = AnsiConsole.Console;
        IAnsiConsole? seenInside = null;

        ConsoleCapture.Run(() => seenInside = AnsiConsole.Console);

        Assert.Same(original, seenInside);
        Assert.Same(original, AnsiConsole.Console);
    }
}
