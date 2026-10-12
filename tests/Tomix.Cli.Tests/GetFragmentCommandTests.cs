using System.Text.Json.Nodes;
using Tomix.Cli.Commands;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;

namespace Tomix.Cli.Tests;

/// <summary>
/// <c>tx get &lt;object&gt; --output-format tmdl|bim|tmsl</c> end to end (#243): the fragment
/// carries the properties the hand-rolled renderer used to drop (description, format string,
/// display folder), and a TMDL table child comes wrapped in <c>ref table</c>.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class GetFragmentCommandTests
{
    private static readonly IReadOnlyList<IModelProvider> Providers = [new TmdlModelProvider()];

    [Fact]
    public void Tmdl_IsAPasteReadyFragment_WithEveryProperty()
    {
        var (exitCode, stdout) = Get("tmdl");

        Assert.Equal(0, exitCode);
        Assert.Equal(
            """
            ref table Sales

            	/// Revenue before returns
            	measure 'Total Sales' = SUM ( Sales[Amount] )
            		formatString: #,0
            		displayFolder: KPIs

            """,
            stdout.ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData("bim")]
    [InlineData("tmsl")]
    public void Json_IsTheObjectAsTheBimCarriesIt(string format)
    {
        var (exitCode, stdout) = Get(format);

        Assert.Equal(0, exitCode);
        var measure = JsonNode.Parse(stdout)!;
        Assert.Equal("Total Sales", measure["name"]!.GetValue<string>());
        Assert.Equal("Revenue before returns", measure["description"]!.GetValue<string>());
        Assert.Equal("#,0", measure["formatString"]!.GetValue<string>());
        Assert.Equal("KPIs", measure["displayFolder"]!.GetValue<string>());
    }

    private static (int ExitCode, string Stdout) Get(string format)
    {
        using var copy = SampleModel.CopyToTemp();
        var table = Path.Combine(copy.Path, "tables", "Sales.tmdl");
        File.WriteAllText(table, File.ReadAllText(table).ReplaceLineEndings("\n").Replace(
            "\tmeasure 'Total Sales' = SUM ( Sales[Amount] )\n",
            "\t/// Revenue before returns\n\tmeasure 'Total Sales' = SUM ( Sales[Amount] )\n\t\tformatString: #,0\n\t\tdisplayFolder: KPIs\n",
            StringComparison.Ordinal));

        var root = TestRoot.With(new GetCommand(Providers, TestServices.Create().State).Build());
        var captured = ConsoleCapture.Run(
            () => root.Parse(["get", "Sales/Total Sales", copy.Path, "--output-format", format]).Invoke());
        return (captured.ExitCode, captured.Stdout);
    }
}
