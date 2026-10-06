using System.CommandLine;
using Tomix.App.Format;
using Tomix.Cli.Commands;

namespace Tomix.Cli.Tests;

/// <summary>
/// A redirected stdin is not proof that anything was piped: CI steps, ssh sessions, and parent
/// processes can hand tx a pipe that never closes. Commands read stdin without <c>-</c> only when
/// it is their only possible input; otherwise reading it to the end would block forever.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class PipedStdinTests
{
    private const string MissingModel = "./no-such-model";

    [Theory]
    [InlineData("format", MissingModel)]
    [InlineData("format", "-m", MissingModel)]
    [InlineData("format", "--path", "Sales/Total")]
    [InlineData("format", "--save")]
    [InlineData("format", "--save-to", "./out")]
    [InlineData("format", "--stage")]
    [InlineData("format", "--revert")]
    [InlineData("add", "Sales/Total", MissingModel)]
    public void CommandWithOtherInput_LeavesPipedStdinAlone(params string[] args)
    {
        var stdin = new RecordingReader("SUM(x)");

        Invoke(stdin, args);

        Assert.False(stdin.WasRead, $"'tx {string.Join(' ', args)}' read stdin.");
    }

    [Fact]
    public void Format_NothingElseGiven_FormatsPipedInput()
    {
        var stdin = new RecordingReader("let a=1 in a\n");

        var captured = Invoke(stdin, "format", "--lang", "m");

        Assert.True(stdin.WasRead);
        Assert.Equal(0, captured.ExitCode);
        Assert.Contains("let", captured.Stdout);
    }

    [Theory]
    [InlineData("format", "-e", "-", "--lang", "m")]
    [InlineData("add", "Sales/Total", MissingModel, "-e", "-")]
    public void Dash_StillReadsStdin(params string[] args)
    {
        var stdin = new RecordingReader("let a=1 in a");

        Invoke(stdin, args);

        Assert.True(stdin.WasRead);
    }

    [Fact]
    public void Query_NoQueryGiven_ReadsPipedInput()
    {
        InputValueResolver.Stdin.Value = new StringReader("EVALUATE ROW(\"a\", 1)\n");
        try
        {
            var (query, error) = QueryCommand.ResolveQueryInput(positional: null, query: null, file: null);

            Assert.Null(error);
            Assert.Equal("EVALUATE ROW(\"a\", 1)", query);
        }
        finally
        {
            InputValueResolver.Stdin.Value = null;
        }
    }

    [Theory]
    [InlineData(null, null, false, true)]
    [InlineData("./model", null, false, false)]
    [InlineData(null, "Sales/Total", false, false)]
    [InlineData(null, null, true, false)]
    public void ReadsPipedExpression_OnlyWhenNothingNamesAModel(string? model, string? path, bool writes, bool expected)
        => Assert.Equal(expected, FormatCommand.ReadsPipedExpression(model, path, writes));

    private static ConsoleCapture.Captured Invoke(TextReader stdin, params string[] args)
    {
        var services = TestServices.Create();
        var root = TestRoot.With(
            new FormatCommand([], new OfflineMFormatterClient(), services.State, services.Mutations).Build(),
            new AddCommand([], services.State, services.Mutations).Build());

        // Set on this async flow only, so a test running in parallel keeps the real stdin.
        InputValueResolver.Stdin.Value = stdin;
        try
        {
            return ConsoleCapture.Invoke(root.Parse(args), captureAnsiConsole: true);
        }
        finally
        {
            InputValueResolver.Stdin.Value = null;
        }
    }

    private sealed class RecordingReader(string text) : StringReader(text)
    {
        public bool WasRead { get; private set; }

        public override string ReadToEnd()
        {
            WasRead = true;
            return base.ReadToEnd();
        }
    }
}
