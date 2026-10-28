using Tomix.App.Test;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;

namespace Tomix.App.Tests;

/// <summary>
/// <c>tx test init</c>: the scaffold it writes (pinned as goldens), how it treats files that are
/// already there, and that what it writes runs under <c>tx test</c> and <c>tx test list</c>.
/// Regenerate the goldens with <c>TOMIX_UPDATE_TEST_INIT_GOLDENS=1</c> and review the diff.
/// </summary>
public sealed class TestInitHandlerTests : IDisposable
{
    private static readonly string[] GenericFiles = ["README.md", "smoke/connection.dax"];

    private static readonly string[] ModelFiles = [.. GenericFiles, "smoke/measure-errors.dax", "smoke/table-rows.dax"];

    private readonly TempDir _dir = new("tomix-testinit-tests");

    public void Dispose() => _dir.Dispose();

    private string TestsPath => _dir.Combine("tests");

    private Task<Tomix.Core.Results.TomixResult<TestInitResult>> Init(bool fromModel = false, bool force = false)
        => new TestInitHandler([new TmdlModelProvider()]).HandleAsync(
            new TestInitRequest(TestsPath, fromModel ? new ModelReference(SampleModel.Locate()) : null, force),
            CancellationToken.None);

    [Fact]
    public async Task Init_WritesTheGenericScaffold_WithoutAModel()
    {
        var result = await Init();

        Assert.True(result.Success);
        Assert.Equal(GenericFiles, result.Data!.Created);
        Assert.Empty(result.Data.Skipped);
        Assert.Equal([$"tx test {TestsPath.Replace('\\', '/')}"], result.Data.NextSteps);
        foreach (var file in GenericFiles)
            AssertGolden(file);
    }

    [Fact]
    public async Task Init_FromModel_AddsMeasureAndTableTests_ThatNeedNoSnapshot()
    {
        var result = await Init(fromModel: true);

        Assert.True(result.Success);
        Assert.Equal(ModelFiles, result.Data!.Created);
        Assert.Equal([$"tx test {TestsPath.Replace('\\', '/')}"], result.Data.NextSteps);
        foreach (var file in ModelFiles)
            AssertGolden(file);
        Assert.Empty(Directory.EnumerateFiles(TestsPath, "*.expected.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Init_SkipsFilesThatAlreadyHoldTheScaffold()
    {
        await Init();
        var again = await Init();

        Assert.True(again.Success);
        Assert.Empty(again.Data!.Created);
        Assert.Equal(GenericFiles, again.Data.Skipped);
    }

    [Fact]
    public async Task Init_AddsToAFolderWithOtherTests()
    {
        _dir.WriteFile("tests/totals/sales.dax", "EVALUATE 'Sales'");

        var result = await Init();

        Assert.True(result.Success);
        Assert.Equal("EVALUATE 'Sales'", File.ReadAllText(_dir.Combine("tests", "totals", "sales.dax")));
    }

    [Fact]
    public async Task Init_RefusesToOverwriteAChangedFile_AndWritesNothing()
    {
        _dir.WriteFile("tests/smoke/connection.dax", "// mine\nEVALUATE ROW ( \"x\", 2 )");

        var result = await Init();

        Assert.False(result.Success);
        Assert.Equal("TOMIX_TEST_INIT_EXISTS", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("smoke/connection.dax", result.Diagnostics[0].Message);
        Assert.Contains("--force", result.Diagnostics[0].Hint);
        Assert.False(File.Exists(_dir.Combine("tests", "README.md")));
    }

    [Fact]
    public async Task Init_Force_OverwritesAChangedFile()
    {
        _dir.WriteFile("tests/smoke/connection.dax", "// mine");

        var result = await Init(force: true);

        Assert.True(result.Success);
        Assert.Contains("smoke/connection.dax", result.Data!.Created);
        AssertGolden("smoke/connection.dax");
    }

    [Fact]
    public async Task Init_RejectsAFilePath()
    {
        _dir.WriteFile("tests", "not a folder");

        var result = await Init();

        Assert.Equal("TOMIX_TEST_INIT_NOT_A_DIRECTORY", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task Init_FromModel_ReportsAModelThatCannotBeOpened()
    {
        var result = await new TestInitHandler([new TmdlModelProvider()]).HandleAsync(
            new TestInitRequest(TestsPath, new ModelReference(_dir.Combine("missing.bim"))), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(Directory.Exists(TestsPath));
    }

    [Fact]
    public async Task Scaffold_IsListedWithItsTags()
    {
        await Init(fromModel: true);

        var list = new TestListHandler().Handle(new TestListRequest(TestsPath, Tags: ["smoke"]));

        Assert.Equal(
            [("smoke/connection", "assertions"), ("smoke/measure-errors", "assertions"), ("smoke/table-rows", "assertions")],
            list.Data!.Tests.Select(test => (test.Name, test.Checks!)));
        Assert.All(list.Data.Tests, test => Assert.Equal(["smoke"], test.Tags));
    }

    [Fact]
    public async Task Scaffold_PassesWithoutAnySnapshot()
    {
        await Init(fromModel: true);

        // The connection test returns one row; the broken-measure and empty-table queries return none.
        var session = new QueryStubs.Session
        {
            OnQuery = request => request.Query.Contains("ISBLANK", StringComparison.Ordinal)
                ? QueryStubs.Rowset()
                : QueryStubs.Rowset(1L)
        };
        var run = await new TestRunHandler([new QueryStubs.Provider(session)], RemoteState).HandleAsync(
            new TestRunRequest(null, null, null, null, TestsPath, Tags: ["smoke"]), CancellationToken.None);

        Assert.Equal(
            [("smoke/connection", TestOutcome.Passed), ("smoke/measure-errors", TestOutcome.Passed), ("smoke/table-rows", TestOutcome.Passed)],
            run.Data!.Tests.Select(test => (test.Name, test.Outcome)));
    }

    // INFO.MEASURES() covers hidden measures too, so one hidden measure is enough.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void MeasureErrors_IsWritten_OnlyWhenTheModelHasAMeasure(bool hasMeasure, bool written)
    {
        var snapshot = Snapshot(Table("Sales", hasMeasure ? [Measure("Secret", hidden: true)] : []));

        Assert.Equal(written, TestInitHandler.HasMeasures(snapshot));
    }

    [Fact]
    public void TableRows_QuotesNames_AndSkipsCalculationGroups()
    {
        var calcGroup = Table("Time Intelligence", [Child("YTD", ModelObjectKind.CalculationItem)]);

        var dax = TestInitHandler.TableRows(Snapshot(Table("O'Brien Sales", []), calcGroup))!;

        Assert.Contains("( \"O'Brien Sales\", COUNTROWS ( 'O''Brien Sales' ) )", dax);
        Assert.DoesNotContain("Time Intelligence", dax);
    }

    private static readonly Func<Tomix.App.State.CliConnectionState?> RemoteState =
        () => new Tomix.App.State.CliConnectionState(
            Server: "powerbi://api.powerbi.com/v1.0/myorg/ws", Database: "MyModel", Model: null, Auth: null,
            Local: false, Profile: null, Workspace: null);

    private static ModelSnapshot Snapshot(params ModelObject[] tables) => new("stub", 1601, tables);

    private static ModelObject Table(string name, IReadOnlyList<ModelObject> children)
        => new(name, ModelObjectKind.Table, name, null, null, null, false, null, children);

    private static ModelObject Measure(string name, bool hidden)
        => new(name, ModelObjectKind.Measure, $"Sales/{name}", null, "1", null, hidden, null, []);

    private static ModelObject Child(string name, ModelObjectKind kind)
        => new(name, kind, name, null, null, null, false, null, []);

    private void AssertGolden(string file)
    {
        var actual = File.ReadAllText(Path.Combine(TestsPath, file));
        var golden = RepoPaths.Combine("tests", "Tomix.App.Tests", "Goldens", "test-init", file + ".approved.txt");
        if (Environment.GetEnvironmentVariable("TOMIX_UPDATE_TEST_INIT_GOLDENS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(golden)!);
            File.WriteAllText(golden, actual);
            return;
        }

        Assert.True(File.Exists(golden), $"Missing test init golden: {golden}. Run with TOMIX_UPDATE_TEST_INIT_GOLDENS=1 and review it.");
        Assert.Equal(File.ReadAllText(golden).ReplaceLineEndings("\n"), actual);
    }
}
