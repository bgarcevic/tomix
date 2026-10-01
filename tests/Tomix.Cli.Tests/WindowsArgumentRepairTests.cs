namespace Tomix.Cli.Tests;

/// <summary>
/// Windows PowerShell 5.1 turns <c>'.\My Model.SemanticModel\'</c> into the argv entry
/// <c>.\My Model.SemanticModel"</c>; the repair restores the path and splits any arguments the
/// lost closing quote glued onto it.
/// </summary>
public sealed class WindowsArgumentRepairTests
{
    private static readonly HashSet<string> Directories = [@".\My Model.SemanticModel", @"C:\models\Sales"];

    private static string[] Repair(params string[] args)
        => WindowsArgumentRepair.Repair(args, Directories.Contains);

    [Theory]
    [InlineData(@".\My Model.SemanticModel""", @".\My Model.SemanticModel\")]
    [InlineData(@"C:\models\Sales""", @"C:\models\Sales\")]
    public void RestoresTrailingSeparator(string damaged, string expected)
        => Assert.Equal(["connect", expected], Repair("connect", damaged));

    [Fact]
    public void SplitsArgumentsGluedOnAfterTheLostQuote()
        => Assert.Equal(
            ["connect", @".\My Model.SemanticModel\", "MyDb", "--local"],
            Repair("connect", @".\My Model.SemanticModel"" MyDb", "--local"));

    [Theory]
    [InlineData(@"EVALUATE ROW(""a"", 1)")]   // several quotes
    [InlineData(@"[Name] = ""x")]            // one quote, but not a directory
    [InlineData(@".\My Model.SemanticModel""x")] // quote not followed by whitespace
    [InlineData(@"""leading")]
    [InlineData(@".\My Model.SemanticModel\")]
    public void LeavesOtherArgumentsUntouched(string arg)
    {
        string[] args = ["query", arg];
        Assert.Same(args, WindowsArgumentRepair.Repair(args, Directories.Contains));
    }
}
