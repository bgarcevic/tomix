using System.Text;
using Spectre.Console;
using Tomix.Cli.Output;

namespace Tomix.Cli.Interactive;

/// <summary>Where <c>tx interactive</c> reads its command lines.</summary>
internal interface ILineReader
{
    /// <summary>The next line, or <c>null</c> at the end of input.</summary>
    /// <param name="promptMarkup">The prompt, as markup; a reader that is not a terminal ignores it.</param>
    string? ReadLine(string promptMarkup);
}

/// <summary>Lines from piped or redirected input: no prompt, no editing.</summary>
internal sealed class RedirectedLineReader(TextReader input) : ILineReader
{
    public string? ReadLine(string promptMarkup) => input.ReadLine();
}

/// <summary>What a key did to the line being edited.</summary>
internal enum LineEditResult
{
    /// <summary>The line changed or the cursor moved; keep reading.</summary>
    Continue,

    /// <summary>Enter: the line is complete.</summary>
    Submit,

    /// <summary>Ctrl-C: the line is abandoned.</summary>
    Cancel,

    /// <summary>Ctrl-D (or Ctrl-Z, as on Windows) on an empty line: no more input.</summary>
    EndOfInput,

    /// <summary>Tab matched several candidates and could not extend the word; show them.</summary>
    ShowCandidates
}

/// <summary>
/// The line being edited at the prompt: its text, cursor, history navigation and tab completion.
/// Pure state, so every key's effect is testable without a terminal.
/// </summary>
internal sealed class LineBuffer
{
    private readonly StringBuilder _text = new();
    private readonly IReadOnlyList<string> _history;
    private readonly Func<string, IReadOnlyList<string>> _complete;
    private int _historyIndex;
    private string _draft = "";

    /// <param name="history">Earlier lines, oldest first; Up walks back through them.</param>
    /// <param name="complete">The completions for a line typed up to the cursor.</param>
    public LineBuffer(IReadOnlyList<string> history, Func<string, IReadOnlyList<string>> complete)
    {
        _history = history;
        _complete = complete;
        _historyIndex = history.Count;
    }

    public string Text => _text.ToString();

    public int Cursor { get; private set; }

    /// <summary>The candidates of the last Tab that returned <see cref="LineEditResult.ShowCandidates"/>.</summary>
    public IReadOnlyList<string> Candidates { get; private set; } = [];

    public LineEditResult Apply(ConsoleKeyInfo key)
    {
        var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                return LineEditResult.Submit;
            case ConsoleKey.C when control:
                return LineEditResult.Cancel;
            case ConsoleKey.D when control:
            case ConsoleKey.Z when control:
                if (_text.Length == 0)
                    return LineEditResult.EndOfInput;
                Delete();
                return LineEditResult.Continue;
            case ConsoleKey.Backspace:
                if (Cursor > 0)
                {
                    _text.Remove(--Cursor, 1);
                }
                return LineEditResult.Continue;
            case ConsoleKey.Delete:
                Delete();
                return LineEditResult.Continue;
            case ConsoleKey.LeftArrow:
                Cursor = Math.Max(0, Cursor - 1);
                return LineEditResult.Continue;
            case ConsoleKey.RightArrow:
                Cursor = Math.Min(_text.Length, Cursor + 1);
                return LineEditResult.Continue;
            case ConsoleKey.Home:
            case ConsoleKey.A when control:
                Cursor = 0;
                return LineEditResult.Continue;
            case ConsoleKey.End:
            case ConsoleKey.E when control:
                Cursor = _text.Length;
                return LineEditResult.Continue;
            case ConsoleKey.Escape:
            case ConsoleKey.U when control:
                Replace("");
                return LineEditResult.Continue;
            case ConsoleKey.UpArrow:
                Recall(-1);
                return LineEditResult.Continue;
            case ConsoleKey.DownArrow:
                Recall(+1);
                return LineEditResult.Continue;
            case ConsoleKey.Tab:
                return Complete();
        }

        if (!char.IsControl(key.KeyChar))
            _text.Insert(Cursor++, key.KeyChar);
        return LineEditResult.Continue;
    }

    private void Delete()
    {
        if (Cursor < _text.Length)
            _text.Remove(Cursor, 1);
    }

    private void Replace(string text)
    {
        _text.Clear().Append(text);
        Cursor = text.Length;
    }

    private void Recall(int direction)
    {
        var next = Math.Clamp(_historyIndex + direction, 0, _history.Count);
        if (next == _historyIndex)
            return;

        // Leaving the line being typed keeps it, so Down can come back to it.
        if (_historyIndex == _history.Count)
            _draft = Text;
        _historyIndex = next;
        Replace(next == _history.Count ? _draft : _history[next]);
    }

    private LineEditResult Complete()
    {
        var typed = Text[..Cursor];
        var wordStart = typed.LastIndexOf(' ') + 1;
        var word = typed[wordStart..];
        var matches = _complete(typed)
            .Where(candidate => candidate.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (matches.Count == 0)
            return LineEditResult.Continue;

        if (matches.Count == 1)
        {
            InsertCompletion(wordStart, matches[0] + " ");
            return LineEditResult.Continue;
        }

        var common = CommonPrefix(matches);
        if (common.Length > word.Length)
        {
            InsertCompletion(wordStart, common);
            return LineEditResult.Continue;
        }

        Candidates = matches;
        return LineEditResult.ShowCandidates;
    }

    private void InsertCompletion(int wordStart, string completion)
    {
        _text.Remove(wordStart, Cursor - wordStart).Insert(wordStart, completion);
        Cursor = wordStart + completion.Length;
    }

    private static string CommonPrefix(IReadOnlyList<string> values)
    {
        var prefix = values[0];
        foreach (var value in values.Skip(1))
        {
            var length = 0;
            while (length < prefix.Length && length < value.Length
                   && char.ToUpperInvariant(prefix[length]) == char.ToUpperInvariant(value[length]))
                length++;
            prefix = prefix[..length];
        }

        return prefix;
    }
}

/// <summary>
/// Reads lines at a terminal: the prompt, line editing, history (Up and Down) and tab completion.
/// Ctrl-C abandons the line rather than ending the process.
/// </summary>
internal sealed class ConsoleLineEditor(Func<string, IReadOnlyList<string>> complete) : ILineReader
{
    private readonly List<string> _history = [];

    public string? ReadLine(string promptMarkup)
    {
        var buffer = new LineBuffer(_history, complete);
        var treatControlC = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        try
        {
            var start = WritePrompt(promptMarkup);
            var drawn = "";
            var cursor = 0;
            while (true)
            {
                var result = buffer.Apply(Console.ReadKey(intercept: true));
                switch (result)
                {
                    case LineEditResult.Submit:
                        MoveTo(start, buffer.Text.Length);
                        Console.WriteLine();
                        Remember(buffer.Text);
                        return buffer.Text;
                    case LineEditResult.Cancel:
                        MoveTo(start, buffer.Text.Length);
                        Console.WriteLine();
                        return "";
                    case LineEditResult.EndOfInput:
                        Console.WriteLine();
                        return null;
                    case LineEditResult.ShowCandidates:
                        MoveTo(start, buffer.Text.Length);
                        Console.WriteLine();
                        AnsiConsole.MarkupLine(Styling.Muted(string.Join("  ", buffer.Candidates)));
                        start = WritePrompt(promptMarkup);
                        drawn = "";
                        cursor = 0;
                        break;
                }

                start = Render(start, drawn, cursor, buffer);
                drawn = buffer.Text;
                cursor = buffer.Cursor;
            }
        }
        finally
        {
            Console.TreatControlCAsInput = treatControlC;
        }
    }

    private void Remember(string line)
    {
        if (!string.IsNullOrWhiteSpace(line) && (_history.Count == 0 || _history[^1] != line))
            _history.Add(line);
    }

    private static (int Left, int Top) WritePrompt(string promptMarkup)
    {
        AnsiConsole.Markup(promptMarkup);
        return (Console.CursorLeft, Console.CursorTop);
    }

    /// <summary>
    /// Brings the screen from <paramref name="drawn"/> (cursor at <paramref name="cursor"/>) to the
    /// buffer's line. Typing or deleting at the end of the line writes only that character, and a
    /// cursor move only moves the cursor, so the cursor never visibly travels; anything else
    /// rewrites the line with the cursor hidden. Returns the prompt's end, which moves up when a
    /// long line scrolled the window.
    /// </summary>
    private static (int Left, int Top) Render((int Left, int Top) start, string drawn, int cursor, LineBuffer buffer)
    {
        var text = buffer.Text;
        var atEnd = cursor == drawn.Length && buffer.Cursor == text.Length;
        if (text == drawn)
        {
            if (buffer.Cursor != cursor)
                MoveTo(start, buffer.Cursor);
            return start;
        }

        if (atEnd && text.Length > drawn.Length && text.StartsWith(drawn, StringComparison.Ordinal))
            return Write(start, text[drawn.Length..], text.Length);

        if (atEnd && text.Length == drawn.Length - 1 && drawn.StartsWith(text, StringComparison.Ordinal) && Console.CursorLeft > 0)
        {
            Console.Write("\b \b");
            return start;
        }

        var visible = CursorVisible(false);
        try
        {
            MoveTo(start, 0);
            start = Write(start, text + new string(' ', Math.Max(0, drawn.Length - text.Length)), Math.Max(text.Length, drawn.Length));
            MoveTo(start, buffer.Cursor);
            return start;
        }
        finally
        {
            CursorVisible(visible);
        }
    }

    /// <summary>Writes <paramref name="text"/> where the cursor is, and corrects the prompt's row
    /// when the line, now <paramref name="length"/> characters, scrolled the window.</summary>
    private static (int Left, int Top) Write((int Left, int Top) start, string text, int length)
    {
        Console.Write(text);
        var width = Math.Max(1, Console.BufferWidth);
        var expectedTop = start.Top + (start.Left + length) / width;
        return Console.CursorTop < expectedTop
            ? (start.Left, start.Top - (expectedTop - Console.CursorTop))
            : start;
    }

    /// <summary>Shows or hides the cursor where the terminal allows it; returns whether it was visible.</summary>
    private static bool CursorVisible(bool visible)
    {
        try
        {
            var was = !OperatingSystem.IsWindows() || Console.CursorVisible;
            Console.CursorVisible = visible;
            return was;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            return true;
        }
    }

    private static void MoveTo((int Left, int Top) start, int index)
    {
        var width = Math.Max(1, Console.BufferWidth);
        var offset = start.Left + index;
        Console.SetCursorPosition(offset % width, Math.Max(0, start.Top + offset / width));
    }
}
