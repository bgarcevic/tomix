using System.CommandLine;
using System.Text.Json.Nodes;
using Tomix.Cli.Commands;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;

namespace Tomix.Cli.Tests;

/// <summary>
/// <c>get</c> is the single read pipeline and <c>ls</c>/<c>deps</c> are shortcuts into it, so a
/// selection read through <c>get</c> must equal the <c>ls</c> output and a <c>get --deps</c> read the
/// <c>deps</c> output byte for byte. The older get/ls projection contract still holds on top: both commands project properties through
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
        var get = CommandJson.Data(Invoke("get", getPath, SampleTmdl, "--output-format", "json"));
        var lsRows = CommandJson.DataArray(Invoke("ls", lsFilter, SampleTmdl, "--output-format", "json"));

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
        var partitions = CommandJson.DataArray(Invoke("ls", "Sales/Partitions", SampleTmdl, "--output-format", "json"));
        var row = Assert.Single(partitions)!.AsObject();
        var path = row["path"]!.GetValue<string>();

        var get = CommandJson.Data(Invoke("get", path, SampleTmdl, "--type", "partition", "--output-format", "json"));

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
        var getHeader = FirstLine(Invoke("get", getPath, SampleTmdl, "--output-format", "csv"));
        var lsHeader = FirstLine(Invoke("ls", lsFilter, SampleTmdl, "--output-format", "csv"));

        Assert.Equal("Path," + getHeader, lsHeader);
    }

    [Fact]
    public void MixedKindCsv_PopulatesGenericColumnsFromObjectFields()
    {
        // `ls Sales` lists a table's children — a mixed column/measure/partition set. The rows'
        // Projected dictionaries are keyed per-kind ("dataType", not "detail"), so the generic
        // CSV columns must come from the GetListObject fields, not the projections.
        var csv = Invoke("ls", "Sales", SampleTmdl, "--output-format", "csv");
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
        var canonical = Invoke("ls", "Sales/Measures", SampleTmdl, "--output-format", "json");
        var legacy = Invoke("ls", SampleTmdl, "Sales/Measures", "--output-format", "json");

        Assert.Equal(canonical, legacy);
        Assert.Contains("Sales/Total Sales", canonical);
    }

    [Fact]
    public void DataType_IsIdenticalAcrossCommands_AndNeverGuessed()
    {
        var get = CommandJson.Data(Invoke("get", "Sales/Total Sales", SampleTmdl, "--output-format", "json"));
        var row = CommandJson.DataArray(Invoke("ls", "Sales/'Total Sales'", SampleTmdl, "--output-format", "json"))
            .Single()!.AsObject();

        // Regression pin: ls used to fabricate "Decimal" from the DAX text while get said "Unknown",
        // and emitted null for detailRowsExpression/kpi where get emitted "".
        Assert.Equal(get["properties"]!["dataType"]!.GetValue<string>(), row["dataType"]!.GetValue<string>());
        foreach (var key in new[] { "detailRowsExpression", "formatStringExpression", "kpi" })
        {
            Assert.NotNull(row[key]);
            Assert.NotNull(get["properties"]![key]);
        }
    }

    public static TheoryData<string[], string[]> Shortcuts => new()
    {
        // get invocation, equivalent shortcut invocation (model appended to both)
        { ["get"], ["ls"] },
        { ["get", "Sa*"], ["ls", "Sa*"] },
        { ["get", "Tables"], ["ls", "Tables"] },
        { ["get", "Sales/Measures"], ["ls", "Sales/Measures"] },
        { ["get", "*/Amount"], ["ls", "*/Amount"] },
        { ["get", "Sales", "--ls"], ["ls", "Sales"] },
        { ["get", "--type", "measure", "--ls"], ["ls", "--type", "measure"] },
        { ["get", "Sales/Total Sales", "--deps"], ["deps", "Sales/Total Sales"] },
        { ["get", "--deps", "Sales/Total Sales"], ["deps", "Sales/Total Sales"] },
        { ["get", "Sales/Amount", "--deps", "downstream"], ["deps", "Sales/Amount", "--downstream"] },
        { ["get", "Sales/Total Sales", "--deps", "upstream", "--deep"], ["deps", "Sales/Total Sales", "--upstream", "--deep"] },
        { ["get", "--unused"], ["deps", "--unused"] },
    };

    [Theory]
    [MemberData(nameof(Shortcuts))]
    public void Shortcut_EqualsGet(string[] get, string[] shortcut)
    {
        string[] json = ["--model", SampleTmdl, "--output-format", "json"];

        Assert.Equal(Invoke([.. shortcut, .. json]), Invoke([.. get, .. json]));
    }

    [Fact]
    public void Get_LonePositionalModel_ListsTables()
        => Assert.Equal(
            Invoke("ls", "--model", SampleTmdl, "--output-format", "json"),
            Invoke("get", SampleTmdl, "--output-format", "json"));

    [Fact]
    public void Where_FiltersTheScope()
    {
        var rows = CommandJson.DataArray(Invoke(
            "get", "Measures", "--model", SampleTmdl, "--where", "Name=total*", "--output-format", "json"));

        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.StartsWith(
            "total", row!["name"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("TOMIX_SINGLE_OBJECT_REQUIRED", "get", "Sa*", "--query", "name")]
    [InlineData("TOMIX_SINGLE_OBJECT_REQUIRED", "get", "Sales/Measures", "--deps")]
    [InlineData("TOMIX_UNUSED_PATH", "get", "Sales", "--unused")]
    [InlineData("TOMIX_INVALID_WHERE", "get", "Measures", "--where", "Name")]
    [InlineData("TOMIX_OUTPUT_FORMAT_UNSUPPORTED", "get", "Sa*", "--output-format", "tmdl")]
    [InlineData("TOMIX_OUTPUT_FORMAT_UNSUPPORTED", "get", "Sales/Amount", "--deps", "--output-format", "csv")]
    [InlineData("TOMIX_USAGE", "get", "Sales/Amount", "--deps", "--unused")]
    [InlineData("TOMIX_USAGE", "get", "Sales", "--ls", "--deps")]
    [InlineData("TOMIX_USAGE", "get", "Sales", "--deep")]
    [InlineData("TOMIX_USAGE", "get", "Sales", "--max-depth", "2")]
    [InlineData("TOMIX_USAGE", "get", "Sales", "--hidden")]
    [InlineData("TOMIX_USAGE", "get", "Sales", "--where", "Name=x", "--all")]
    [InlineData("TOMIX_USAGE", "get", "Sales/Amount", "--deps", "sideways")]
    public void Get_RejectsOptionsThatCannotApply(string code, params string[] args)
    {
        var captured = InvokeRaw([.. args, "--model", SampleTmdl, "--error-format", "json"]);

        Assert.Equal(2, captured.ExitCode);
        Assert.Contains(code, captured.Stderr);
    }

    private static string Invoke(params string[] args)
    {
        var captured = InvokeRaw(args);
        Assert.True(captured.ExitCode == 0,
            $"'{string.Join(' ', args)}' exited {captured.ExitCode}: {captured.Stderr}");
        return captured.Stdout;
    }

    private static ConsoleCapture.Captured InvokeRaw(string[] args)
    {
        var services = TestServices.Create();
        var root = TestRoot.With(args[0] switch
        {
            "get" => new GetCommand(Providers, services.State).Build(),
            "deps" => new DepsCommand(Providers, services.State).Build(),
            _ => new LsCommand(Providers, services.State).Build()
        });

        return ConsoleCapture.Invoke(root.Parse(args));
    }

    private static readonly string SampleTmdl = SampleModel.Locate();

    private static string FirstLine(string output)
        => output.Split('\n')[0].TrimEnd('\r');

}
