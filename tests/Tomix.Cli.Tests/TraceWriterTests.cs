using Tomix.Cli.Output;

namespace Tomix.Cli.Tests;

// Asserts Console.Error identity, so it must not run concurrently with tests that swap the
// process-global writer via Console.SetError (see ConsoleStateCollection).
[Collection(ConsoleStateCollection.Name)]
public class TraceWriterTests
{
    [Theory]
    [InlineData(null, "-")]      // bare --trace (ZeroOrOne GetValue returns null) -> stderr
    [InlineData("", "-")]        // empty value -> stderr
    [InlineData("-", "-")]       // explicit "-" -> stderr
    [InlineData("trace.log", "trace.log")]   // file path -> file
    [InlineData("C:/tmp/x.log", "C:/tmp/x.log")] // absolute path preserved
    public void ResolvePath_Normalizes_Bare_And_Keeps_Paths(string? input, string expected)
        => Assert.Equal(expected, TraceWriter.ResolvePath(input));

    [Fact]
    public void Open_Returns_Null_When_Path_Null()
        => Assert.Null(TraceWriter.Open(tracePath: null, quiet: false));

    [Fact]
    public void Open_Returns_ConsoleError_For_Stderr_Path()
    {
        // "-" is the canonical "stderr" sentinel. The writer is wrapped so that disposing it
        // (e.g. via `using`) does not dispose the process-shared Console.Error.
        using (var writer = TraceWriter.Open("-", quiet: false))
        {
            var wrapper = Assert.IsType<NonDisposingTextWriter>(writer);
            Assert.Same(Console.Error, wrapper.Inner);
        }

        // Console.Error must still be usable after the wrapper's `using` scope ends.
        Console.Error.WriteLine("probe: stderr survived trace-writer disposal");
    }

    [Fact]
    public void Open_Returns_NullWriter_When_Stderr_And_Quiet()
    {
        // --quiet + --trace should still parse but suppress stderr output to avoid interleaving.
        using var writer = TraceWriter.Open("-", quiet: true);
        Assert.Same(TextWriter.Null, writer);
    }

    [Fact]
    public void Open_Opens_File_For_Path()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tomix-trace-{Guid.NewGuid():N}.log");
        try
        {
            // Dispose before reading so the file handle is released (AutoFlush alone doesn't
            // close the stream; the OS still sees it as in use until Dispose).
            using (var writer = TraceWriter.Open(path, quiet: false))
            {
                Assert.NotNull(writer);
                writer!.WriteLine("probe");
            }

            Assert.Contains("probe", File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
