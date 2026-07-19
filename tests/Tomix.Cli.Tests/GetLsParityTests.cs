using System.CommandLine;
using System.Text.Json.Nodes;
using Tomix.Cli.Commands;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;

namespace Tomix.Cli.Tests;

/// <summary>
/// The get/ls parity contract: both commands project properties through
/// <c>ModelPropertyCatalog</c>, so for the same object an ls JSON row (minus its
/// <c>path</c>/<c>type</c> envelope) must equal get's <c>properties</c> object, and CSV headers
/// must match modulo ls's leading <c>Path</c> column. A failure here means one command's output
/// was changed without the other — fix it in the catalog, not the command.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class GetLsParityTests
{
    private static readonly IReadOnlyList<IModelProvider> Providers = [new TmdlModelProvider()];

    public static TheoryData<string, string> ObjectPaths => new()
    {
        // get path, ls path-filter resolving to exactly that object
        { "Sales", "Sale*" },                         // table (ls exact literal would list children)
        { "Sales/Total Sales", "Sales/'Total Sales'" },
        { "Sales/Amount", "Sales/Amount*" },
        { "Customers", "Customer*" }
    };

    [Theory]
    [MemberData(nameof(ObjectPaths))]
    public void LsRow_EqualsGetProperties(string getPath, string lsFilter)
    {
        var get = JsonNode.Parse(Invoke("get", getPath, SampleModel, "--output-format", "json"))!;
        var lsRows = JsonNode.Parse(Invoke("ls", lsFilter, SampleModel, "--output-format", "json"))!.AsArray();

        var row = Assert.Single(lsRows)!.AsObject();
        Assert.Equal(get["path"]!.GetValue<string>(), row["path"]!.GetValue<string>());
        Assert.Equal(get["type"]!.GetValue<string>(), row["type"]!.GetValue<string>());

        row.Remove("path");
        row.Remove("type");
        Assert.True(
            JsonNode.DeepEquals(get["properties"], row),
            $"ls row and get properties differ for '{getPath}':\nget: {get["properties"]}\nls:  {row}");
    }

    [Fact]
    public void LsRow_EqualsGetProperties_ForPartitions()
    {
        var partitions = JsonNode.Parse(Invoke("ls", "Sales/Partitions", SampleModel, "--output-format", "json"))!.AsArray();
        var row = Assert.Single(partitions)!.AsObject();
        var path = row["path"]!.GetValue<string>();

        var get = JsonNode.Parse(Invoke("get", path, SampleModel, "--type", "partition", "--output-format", "json"))!;

        row.Remove("path");
        row.Remove("type");
        Assert.True(
            JsonNode.DeepEquals(get["properties"], row),
            $"ls row and get properties differ for partition '{path}'");
    }

    [Theory]
    [InlineData("Sales", "Sale*")]
    [InlineData("Sales/Total Sales", "Sales/Measures")]
    [InlineData("Sales/Amount", "Sales/Columns")]
    public void CsvHeaders_Match_ModuloLeadingPath(string getPath, string lsFilter)
    {
        var getHeader = FirstLine(Invoke("get", getPath, SampleModel, "--output-format", "csv"));
        var lsHeader = FirstLine(Invoke("ls", lsFilter, SampleModel, "--output-format", "csv"));

        Assert.Equal("Path," + getHeader, lsHeader);
    }

    [Fact]
    public void MixedKindCsv_PopulatesGenericColumnsFromObjectFields()
    {
        // `ls Sales` lists a table's children — a mixed column/measure/partition set. The rows'
        // Projected dictionaries are keyed per-kind ("dataType", not "detail"), so the generic
        // CSV columns must come from the LsObject fields, not the projections.
        var csv = Invoke("ls", "Sales", SampleModel, "--output-format", "csv");
        var lines = csv.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        Assert.Equal("Path,Name,Description,Hidden,Detail,Expression", lines[0]);

        var amount = Assert.Single(lines, l => l.StartsWith("Sales/Amount,", StringComparison.Ordinal));
        Assert.Contains("decimal", amount);       // column Detail = data type, previously blank
        var measure = Assert.Single(lines, l => l.StartsWith("Sales/Total Sales,", StringComparison.Ordinal));
        Assert.Contains("SUM", measure);          // measure Expression survives
    }

    [Fact]
    public void Ls_AcceptsFilterModelOrder_AndLegacyModelFilterOrder()
    {
        // Canonical order matches get: `ls [path-filter] [model]`. The legacy
        // `ls <model> [path-filter]` order stays accepted via the can-open heuristic.
        // (JSON output on purpose: AnsiConsole-backed text output caches the console
        // writer from the first invoke, so captured text is unreliable across invokes.)
        var canonical = Invoke("ls", "Sales/Measures", SampleModel, "--output-format", "json");
        var legacy = Invoke("ls", SampleModel, "Sales/Measures", "--output-format", "json");

        Assert.Equal(canonical, legacy);
        Assert.Contains("Sales/Total Sales", canonical);
    }

    [Fact]
    public void DataType_IsIdenticalAcrossCommands_AndNeverGuessed()
    {
        var get = JsonNode.Parse(Invoke("get", "Sales/Total Sales", SampleModel, "--output-format", "json"))!;
        var row = JsonNode.Parse(Invoke("ls", "Sales/'Total Sales'", SampleModel, "--output-format", "json"))!
            .AsArray().Single()!.AsObject();

        // Regression pin: ls used to fabricate "Decimal" from the DAX text while get said "Unknown",
        // and emitted null for detailRowsExpression/kpi where get emitted "".
        Assert.Equal(get["properties"]!["dataType"]!.GetValue<string>(), row["dataType"]!.GetValue<string>());
        foreach (var key in new[] { "detailRowsExpression", "formatStringExpression", "kpi" })
        {
            Assert.NotNull(row[key]);
            Assert.NotNull(get["properties"]![key]);
        }
    }

    private static string Invoke(params string[] args)
    {
        var root = new RootCommand("test");
        foreach (var option in GlobalOptions.All())
            root.Options.Add(option);
        var services = TestServices.Create();
        root.Subcommands.Add(args[0] == "get"
            ? new GetCommand(Providers, services.State).Build()
            : new LsCommand(Providers, services.State).Build());

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exitCode = root.Parse(args).Invoke();
            Assert.True(exitCode == 0, $"'{string.Join(' ', args)}' exited {exitCode}: {stderr}");
            return stdout.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static string FirstLine(string output)
        => output.Split('\n')[0].TrimEnd('\r');

    private static string SampleModel { get; } = FindSampleModel();

    private static string FindSampleModel()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "basic-tmdl");
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new InvalidOperationException("samples/basic-tmdl not found above test base directory.");
    }
}
