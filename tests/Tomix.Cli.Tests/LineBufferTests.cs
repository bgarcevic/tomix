using Tomix.Cli.Interactive;

namespace Tomix.Cli.Tests;

/// <summary>The prompt's line editing in <c>tx interactive</c>, key by key.</summary>
public sealed class LineBufferTests
{
    private static readonly string[] Commands = ["set", "save", "status", "summary", "undo"];

    [Fact]
    public void Typing_InsertsAtTheCursor()
    {
        var buffer = Type("st", "");

        buffer.Apply(Key(ConsoleKey.LeftArrow));
        Apply(buffer, "e");

        Assert.Equal("set", buffer.Text);
        Assert.Equal(2, buffer.Cursor);
    }

    [Theory]
    [InlineData(ConsoleKey.Backspace, "sav", 3)]
    [InlineData(ConsoleKey.Home, "save", 0)]
    [InlineData(ConsoleKey.Escape, "", 0)]
    public void EditingKeys(ConsoleKey key, string text, int cursor)
    {
        var buffer = Type("save", "");

        buffer.Apply(Key(key));

        Assert.Equal(text, buffer.Text);
        Assert.Equal(cursor, buffer.Cursor);
    }

    [Fact]
    public void Delete_RemovesUnderTheCursor()
    {
        var buffer = Type("save", "");
        buffer.Apply(Key(ConsoleKey.Home));

        buffer.Apply(Key(ConsoleKey.Delete));

        Assert.Equal("ave", buffer.Text);
    }

    [Fact]
    public void UpAndDown_WalkHistory_AndKeepTheLineBeingTyped()
    {
        var buffer = new LineBuffer(["status", "undo"], _ => []);
        Apply(buffer, "sa");

        buffer.Apply(Key(ConsoleKey.UpArrow));
        Assert.Equal("undo", buffer.Text);
        buffer.Apply(Key(ConsoleKey.UpArrow));
        buffer.Apply(Key(ConsoleKey.UpArrow));
        Assert.Equal("status", buffer.Text);
        buffer.Apply(Key(ConsoleKey.DownArrow));
        buffer.Apply(Key(ConsoleKey.DownArrow));

        Assert.Equal("sa", buffer.Text);
    }

    [Fact]
    public void Tab_OneMatch_CompletesTheWord()
    {
        var buffer = Type("un", Commands);

        Assert.Equal(LineEditResult.Continue, buffer.Apply(Key(ConsoleKey.Tab)));

        Assert.Equal("undo ", buffer.Text);
    }

    [Fact]
    public void Tab_SeveralMatches_ExtendsToTheirCommonPrefix()
    {
        var buffer = Type("sta", ["status", "stage"]);

        buffer.Apply(Key(ConsoleKey.Tab));

        Assert.Equal("sta", buffer.Text);
        Assert.Equal(LineEditResult.ShowCandidates, buffer.Apply(Key(ConsoleKey.Tab)));
        Assert.Equal(["status", "stage"], buffer.Candidates);

        var su = Type("su", ["summary", "summarize"]);
        su.Apply(Key(ConsoleKey.Tab));
        Assert.Equal("summar", su.Text);
    }

    [Fact]
    public void Tab_CompletesOnlyTheWordAtTheCursor()
    {
        var buffer = new LineBuffer([], typed => typed.StartsWith("set ", StringComparison.Ordinal) ? ["Sales/Amount"] : []);
        Apply(buffer, "set Sa");

        buffer.Apply(Key(ConsoleKey.Tab));

        Assert.Equal("set Sales/Amount ", buffer.Text);
    }

    [Fact]
    public void CtrlD_OnAnEmptyLine_EndsInput()
        => Assert.Equal(LineEditResult.EndOfInput, Type("", "").Apply(Control(ConsoleKey.D)));

    [Fact]
    public void CtrlC_AbandonsTheLine()
        => Assert.Equal(LineEditResult.Cancel, Type("save", "").Apply(Control(ConsoleKey.C)));

    private static ConsoleKeyInfo Control(ConsoleKey key) => new('\0', key, shift: false, alt: false, control: true);

    [Fact]
    public void Enter_Submits()
        => Assert.Equal(LineEditResult.Submit, Type("undo", "").Apply(Key(ConsoleKey.Enter)));

    private static LineBuffer Type(string text, params string[] completions)
    {
        var buffer = new LineBuffer([], _ => completions);
        Apply(buffer, text);
        return buffer;
    }

    private static void Apply(LineBuffer buffer, string text)
    {
        foreach (var character in text)
            buffer.Apply(new ConsoleKeyInfo(character, ConsoleKey.A, shift: false, alt: false, control: false));
    }

    private static ConsoleKeyInfo Key(ConsoleKey key)
        => new(key == ConsoleKey.Enter ? '\r' : '\0', key, shift: false, alt: false, control: false);
}
