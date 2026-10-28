using Tomix.App.State;
using Tomix.App.Test;

namespace Tomix.App.Tests;

/// <summary>
/// <c>tags=</c> directives: parsing, <c>tx test --tag</c> selection, and <c>tx test list</c>.
/// </summary>
public sealed class TestTagsTests : IDisposable
{
    private static readonly Func<CliConnectionState?> RemoteState =
        () => new CliConnectionState(
            Server: "powerbi://api.powerbi.com/v1.0/myorg/ws", Database: "MyModel", Model: null, Auth: null,
            Local: false, Profile: null, Workspace: null);

    private readonly TempDir _dir = new("tomix-testtags-tests");

    public void Dispose() => _dir.Dispose();

    private void WriteDax(string name, string header)
        => _dir.WriteFile(name + ".dax", header + "\nEVALUATE 'Sales'");

    private Task<Tomix.Core.Results.TomixResult<TestRunResult>> Run(params string[] tags)
        => new TestRunHandler([new QueryStubs.Provider(new QueryStubs.Session { OnQuery = _ => QueryStubs.Rowset(1L) })], RemoteState)
            .HandleAsync(new TestRunRequest(null, null, null, null, _dir.Path, Tags: tags.Length > 0 ? tags : null), CancellationToken.None);

    private TestListResult List(string? filter = null, params string[] tags)
        => new TestListHandler().Handle(new TestListRequest(_dir.Path, filter, tags.Length > 0 ? tags : null)).Data!;

    // ── Parsing ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("// tx: tags=smoke", new[] { "smoke" })]
    [InlineData("// tx: tags=smoke,nightly", new[] { "smoke", "nightly" })]
    [InlineData("// tx: rows=1 tags=smoke,nightly ignore-row-order", new[] { "smoke", "nightly" })]
    [InlineData("// tx: tags=smoke\n// tx: tags=Smoke,slow_1", new[] { "smoke", "slow_1" })]
    [InlineData("// tx: tolerance=0.01", new string[0])]
    public void ReadDirectives_ReadsTags(string header, string[] tags)
    {
        var directives = TestCompareOptions.Exact.ReadDirectives(header + "\nEVALUATE T", out var error);

        Assert.Null(error);
        Assert.Equal(tags, directives!.Tags);
    }

    [Fact]
    public void ReadDirectives_KeepsTheOtherDirectivesOnATagLine()
    {
        var directives = TestCompareOptions.Exact.ReadDirectives("// tx: tags=a,b rows>=1 ignore-row-order\nEVALUATE T", out _);

        Assert.Equal(1, directives!.Assertions.MinRows);
        Assert.True(directives.Compare.IgnoreRowOrder);
    }

    [Theory]
    [InlineData("// tx: tags=", "it takes tag names")]
    [InlineData("// tx: tags=smoke,ni ght", "Unknown directive 'ght'")]
    [InlineData("// tx: tags=smo.ke", "tag 'smo.ke' may hold only")]
    [InlineData("// tx: tags", "Unknown directive 'tags'")]
    public void ReadDirectives_RejectsBadTags(string header, string message)
    {
        Assert.Null(TestCompareOptions.Exact.ReadDirectives(header + "\nEVALUATE T", out var error));
        Assert.Contains(message, error);
    }

    // ── tx test --tag ───────────────────────────────────────────────────────

    [Fact]
    public async Task Run_WithTags_RunsOnlyTestsCarryingAnyOfThem()
    {
        WriteDax("a", "// tx: tags=smoke rows=1");
        WriteDax("b", "// tx: tags=nightly rows=1");
        WriteDax("c", "// tx: rows=1");

        var result = await Run("SMOKE", "other");

        Assert.Equal(["a"], result.Data!.Tests.Select(test => test.Name));
    }

    [Fact]
    public async Task Run_WithTags_KeepsATestWhoseDirectivesAreInvalid_SoItsErrorShows()
    {
        WriteDax("broken", "// tx: rows=many");

        var result = await Run("smoke");

        Assert.Equal(TestOutcome.Error, Assert.Single(result.Data!.Tests).Outcome);
    }

    [Fact]
    public async Task Run_WithTags_FailsAsNoneFound_NamingTheTag()
    {
        WriteDax("a", "// tx: rows=1");

        var result = await Run("smoke");

        Assert.Equal("TOMIX_TEST_NONE_FOUND", result.Diagnostics[0].Code);
        Assert.Contains("match --tag smoke under", result.Diagnostics[0].Message);
    }

    [Fact]
    public async Task Run_WithTags_ReportsNoOrphans()
    {
        WriteDax("a", "// tx: tags=smoke rows=1");
        _dir.WriteFile("gone.expected.json", "{}");

        Assert.Empty((await Run("smoke")).Data!.Orphans!);
        Assert.Single((await Run()).Data!.Orphans!);
    }

    // ── tx test list ────────────────────────────────────────────────────────

    [Fact]
    public void List_SaysHowEachTestIsDecided()
    {
        WriteDax("assert", "// tx: rows=1");
        WriteDax("both", "// tx: columns=[A]\n// tx: snapshot");
        WriteDax("error", "// tx: expect-error");
        WriteDax("invalid", "// tx: rows=many");
        WriteDax("plain", "// just a comment");
        _dir.WriteFile("plain.expected.json", "{}");

        var tests = List().Tests;

        Assert.Equal(
            [
                ("assert", "assertions", false, false),
                ("both", "assertions+snapshot", false, false),
                ("error", "expect-error", false, false),
                ("invalid", null, false, true),
                ("plain", "snapshot", true, false),
            ],
            tests.Select(test => (test.Name, test.Checks, test.HasSnapshot, test.Error is not null)));
    }

    [Fact]
    public void List_FiltersByNameAndTag()
    {
        WriteDax("totals/sales", "// tx: tags=smoke");
        WriteDax("totals/margin", "// tx: tags=nightly");
        WriteDax("other", "// tx: tags=smoke");

        Assert.Equal(["totals/sales"], List("totals/*", "smoke").Tests.Select(test => test.Name));
        Assert.Equal(["other", "totals/sales"], List(null, "smoke").Tests.Select(test => test.Name));
    }

    [Fact]
    public void List_FailsOnAMissingPath()
    {
        var result = new TestListHandler().Handle(new TestListRequest(_dir.Combine("nope")));

        Assert.Equal("TOMIX_TEST_PATH_NOT_FOUND", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void List_IsEmpty_ForAFolderWithoutTests()
        => Assert.Empty(List().Tests);
}
