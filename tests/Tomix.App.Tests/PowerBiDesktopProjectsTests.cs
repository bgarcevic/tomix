using Tomix.App.Connect;

namespace Tomix.App.Tests;

public sealed class PowerBiDesktopProjectsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tomix-pbi-projects-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// Writes a PBIP the way Power BI Desktop does: <c>&lt;name&gt;.pbip</c> names
    /// <c>&lt;name&gt;.Report</c>, whose <c>definition.pbir</c> names the model folder by path.
    /// </summary>
    private string WriteProject(string name, string modelFolder)
    {
        var report = Path.Combine(_dir, $"{name}.Report");
        Directory.CreateDirectory(report);
        Directory.CreateDirectory(Path.Combine(_dir, modelFolder, "definition"));
        File.WriteAllText(Path.Combine(report, "definition.pbir"),
            $$"""{ "version": "4.0", "datasetReference": { "byPath": { "path": "../{{modelFolder}}" } } }""");
        var pbip = Path.Combine(_dir, $"{name}.pbip");
        File.WriteAllText(pbip, $$"""{ "version": "1.0", "artifacts": [ { "report": { "path": "{{name}}.Report" } } ] }""");
        return pbip;
    }

    [Theory]
    [InlineData("Sales.pbip")]
    [InlineData("Sales.SemanticModel")]
    [InlineData("Sales.SemanticModel/")]
    [InlineData("Sales.SemanticModel/definition")]
    [InlineData("Sales.SemanticModel/definition.pbism")]
    public void Opens_ThePbipItsModelFolderAndAnythingInIt(string modelPath)
    {
        var pbip = WriteProject("Sales", "Sales.SemanticModel");

        Assert.True(PowerBiDesktopProjects.Opens(pbip, Path.Combine(_dir, modelPath)));
    }

    [Theory]
    [InlineData("Other.SemanticModel")]   // another project in the same folder
    [InlineData("Sales.Report")]          // the report, not the model
    [InlineData("Sales.SemanticModelX")]  // a prefix of the folder name is not inside it
    [InlineData(".")]                     // the folder holding the project
    public void Opens_NotAnotherModelBesideIt(string modelPath)
    {
        var pbip = WriteProject("Sales", "Sales.SemanticModel");
        WriteProject("Other", "Other.SemanticModel");

        Assert.False(PowerBiDesktopProjects.Opens(pbip, Path.Combine(_dir, modelPath)));
    }

    [Fact]
    public void Opens_FollowsTheReportToAModelFolderWithAnotherName()
    {
        var pbip = WriteProject("Sales", "Shared Model.SemanticModel");

        Assert.True(PowerBiDesktopProjects.Opens(pbip, Path.Combine(_dir, "Shared Model.SemanticModel")));
    }

    [Fact]
    public void Opens_FallsBackToDesktopsLayoutWhenTheProjectCannotBeRead()
    {
        var pbip = Path.Combine(_dir, "Sales.pbip");
        File.WriteAllText(pbip, "not json");

        Assert.True(PowerBiDesktopProjects.Opens(pbip, Path.Combine(_dir, "Sales.SemanticModel")));
    }

    [Fact]
    public void Opens_AStandaloneModelByItsPbism()
    {
        var pbism = Path.Combine(_dir, "Sales.SemanticModel", "definition.pbism");

        Assert.True(PowerBiDesktopProjects.Opens(pbism, Path.Combine(_dir, "Sales.SemanticModel", "definition")));
    }

    [Fact]
    public void Opens_NothingForAPbix()
        => Assert.False(PowerBiDesktopProjects.Opens(Path.Combine(_dir, "Sales.pbix"), Path.Combine(_dir, "Sales.SemanticModel")));

    [Fact]
    public void FindOpening_TheOneInstanceWithTheModelOpen()
    {
        var sales = WriteProject("Sales", "Sales.SemanticModel");
        var other = WriteProject("Other", "Other.SemanticModel");
        PowerBiDesktopInstance[] instances =
        [
            new("localhost:1", "Other", "a.txt", other),
            new("localhost:2", null, "b.txt"),
            new("localhost:3", "Sales", "c.txt", sales)
        ];

        Assert.Equal("localhost:3", PowerBiDesktopProjects.FindOpening(Path.Combine(_dir, "Sales.SemanticModel"), instances)?.Endpoint);
        Assert.Null(PowerBiDesktopProjects.FindOpening(Path.Combine(_dir, "Third.SemanticModel"), instances));
    }

    [Fact]
    public void FindOpening_NoneWhenTwoInstancesHaveItOpen()
    {
        var sales = WriteProject("Sales", "Sales.SemanticModel");

        Assert.Null(PowerBiDesktopProjects.FindOpening(
            Path.Combine(_dir, "Sales.SemanticModel"),
            [new("localhost:1", "Sales", "a.txt", sales), new("localhost:2", "Sales", "b.txt", sales)]));
    }
}
