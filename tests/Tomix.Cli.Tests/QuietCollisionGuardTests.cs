using System.CommandLine;
using Tomix.Cli.Commands;

namespace Tomix.Cli.Tests;

/// <summary>
/// Issue #218: the global <c>-q</c>/<c>--quiet</c> flag never consumes a value, so on
/// <c>query</c> and <c>get</c> the text a user pastes right after it binds to a positional
/// argument (the query text, or the optional [model] path) and the command misreads the
/// invocation. The guard rejects exactly that shape; every row here parses clean, so the
/// rejection comes from the guard and not from the parser.
/// </summary>
public sealed class QuietCollisionGuardTests
{
    [Theory]
    [InlineData(new[] { "query", "-q", "EVALUATE x" }, true)]
    [InlineData(new[] { "query", "--quiet", "EVALUATE x" }, true)]
    [InlineData(new[] { "get", "Sales", "-q", "expression" }, true)]
    [InlineData(new[] { "get", "Sales", "--quiet", "expression" }, true)]
    // A quiet flag after the positional text, or with no text to swallow, is plain quiet usage.
    [InlineData(new[] { "query", "EVALUATE x", "--quiet" }, false)]
    [InlineData(new[] { "query", "-q", "--query", "EVALUATE x" }, false)]
    [InlineData(new[] { "query", "-q" }, false)]
    // get's [model] positional only collides when the required path was already given before -q;
    // a leading -q before both positionals binds them unambiguously and stays quiet usage.
    [InlineData(new[] { "get", "-q", "Sales" }, false)]
    [InlineData(new[] { "get", "-q", "Sales", "OtherModel" }, false)]
    // Commands without a collision rule never reject — -q is quiet everywhere else.
    [InlineData(new[] { "ls", "-q", "Sales" }, false)]
    public void QuietFollowedBySwallowedPositional_IsRejectedOnlyForDocumentedCommands(string[] args, bool expectCollision)
    {
        var parsed = TestRoot.Full().Parse(args);

        Assert.Empty(parsed.Errors);
        var collision = QuietCollisionGuard.FindCollision(parsed);
        Assert.Equal(expectCollision, collision is not null);
        if (expectCollision)
            Assert.Equal("TOMIX_QUIET_COLLISION", collision!.Code);
    }

    [Collection(ConsoleStateCollection.Name)]
    public sealed class TryRejectWrites
    {
        [Fact]
        public void Collision_CarriesCodeInJson()
        {
            var parsed = TestRoot.Full().Parse(["query", "-q", "EVALUATE x", "--error-format", "json"]);

            var captured = ConsoleCapture.Run(() => QuietCollisionGuard.TryReject(parsed) ? 2 : 0);

            Assert.Equal("TOMIX_QUIET_COLLISION",
                System.Text.Json.JsonDocument.Parse(captured.Stderr).RootElement.GetProperty("code").GetString());
        }

        [Fact]
        public void NoCollision_WritesNothingAndReturnsFalse()
        {
            var parsed = TestRoot.Full().Parse(["ls", "-q", "Sales"]);

            var captured = ConsoleCapture.Run(() => QuietCollisionGuard.TryReject(parsed) ? 2 : 0);

            Assert.DoesNotContain("TOMIX_QUIET_COLLISION", captured.Stderr);
        }
    }
}
