using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Tomix.Cli.Serve;

/// <summary>
/// Lets several protocol requests run commands at once, each with its own captured output. While
/// installed, <see cref="Console.Out"/>, <see cref="Console.Error"/> and
/// <see cref="AnsiConsole.Console"/> write to the writers of the current <see cref="Capture"/>
/// (an async-local scope), and outside any capture to the fallback, which is the server's log:
/// stdout may carry the protocol, so nothing else is ever written to it.
/// </summary>
internal static class ConsoleRouting
{
    private static readonly AsyncLocal<Target?> Current = new();

    /// <summary>Routes the console until the result is disposed, then restores it.</summary>
    public static IDisposable Install(TextWriter fallback)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var originalIn = Console.In;
        var originalAnsi = AnsiConsole.Console;
        var fallbackTarget = new Target(fallback, fallback);
        Console.SetOut(new RoutedWriter(target => target.Out, fallbackTarget));
        Console.SetError(new RoutedWriter(target => target.Error, fallbackTarget));
        // stdin may carry the protocol too; nothing a command does may read it.
        Console.SetIn(TextReader.Null);
        AnsiConsole.Console = new RoutedAnsiConsole(fallbackTarget);
        return new Restore(() =>
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Console.SetIn(originalIn);
            AnsiConsole.Console = originalAnsi;
        });
    }

    /// <summary>Runs <paramref name="run"/> with the console written to <paramref name="stdout"/> and <paramref name="stderr"/>.</summary>
    public static async Task<T> CaptureAsync<T>(TextWriter stdout, TextWriter stderr, Func<Task<T>> run)
    {
        var previous = Current.Value;
        Current.Value = new Target(stdout, stderr);
        try
        {
            return await run();
        }
        finally
        {
            Current.Value = previous;
        }
    }

    private sealed class Target(TextWriter output, TextWriter error)
    {
        public TextWriter Out { get; } = TextWriter.Synchronized(output);
        public TextWriter Error { get; } = TextWriter.Synchronized(error);

        public IAnsiConsole Ansi { get; } = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(output),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No
        });
    }

    private sealed class RoutedWriter(Func<Target, TextWriter> select, Target fallback) : TextWriter
    {
        private TextWriter Writer => select(Current.Value ?? fallback);

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => Writer.Write(value);

        public override void Write(string? value) => Writer.Write(value);

        public override void Write(char[] buffer, int index, int count) => Writer.Write(buffer, index, count);

        public override void WriteLine(string? value) => Writer.WriteLine(value);

        public override void Flush() => Writer.Flush();
    }

    private sealed class RoutedAnsiConsole(Target fallback) : IAnsiConsole
    {
        private IAnsiConsole Console => (Current.Value ?? fallback).Ansi;

        public Profile Profile => Console.Profile;

        public IAnsiConsoleCursor Cursor => Console.Cursor;

        public IAnsiConsoleInput Input => Console.Input;

        public IExclusivityMode ExclusivityMode => Console.ExclusivityMode;

        public RenderPipeline Pipeline => Console.Pipeline;

        public void Clear(bool home) => Console.Clear(home);

        public void Write(IRenderable renderable) => Console.Write(renderable);

        public void WriteAnsi(Action<AnsiWriter> action) => Console.WriteAnsi(action);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        private Action? _restore = restore;

        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
