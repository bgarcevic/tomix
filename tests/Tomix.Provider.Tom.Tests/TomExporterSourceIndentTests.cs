using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// Verifies that TMDL export matches Power BI Desktop's partition <c>source =</c> block
/// convention (body one level below the property) instead of TmdlSerializer's two-level
/// convention, so saving a Desktop-authored model does not rewrite every table file.
/// </summary>
public sealed class TomExporterSourceIndentTests
{
    [Fact]
    public void OutdentSourceBlocks_MovesSerializerBlockToDesktopDepth()
    {
        var input =
            "\tpartition Sales = m\n" +
            "\t\tmode: import\n" +
            "\t\tsource =\n" +
            "\t\t\t\tlet\n" +
            "\t\t\t\t\tSource = #table({}, {})\n" +
            "\t\t\t\tin\n" +
            "\t\t\t\t\tSource\n" +
            "\n" +
            "\tannotation A = 1\n";

        var expected =
            "\tpartition Sales = m\n" +
            "\t\tmode: import\n" +
            "\t\tsource =\n" +
            "\t\t\tlet\n" +
            "\t\t\t\tSource = #table({}, {})\n" +
            "\t\t\tin\n" +
            "\t\t\t\tSource\n" +
            "\n" +
            "\tannotation A = 1\n";

        Assert.Equal(expected, TomModelExporter.OutdentSourceBlocks(input));
    }

    [Fact]
    public void OutdentSourceBlocks_IsIdempotent_OnDesktopDepthBlocks()
    {
        var desktop =
            "\tpartition Sales = m\n" +
            "\t\tmode: import\n" +
            "\t\tsource =\n" +
            "\t\t\tlet\n" +
            "\t\t\t\tSource = #table({}, {})\n" +
            "\t\t\tin\n" +
            "\t\t\t\tSource\n";

        Assert.Same(desktop, TomModelExporter.OutdentSourceBlocks(desktop));
    }

    [Fact]
    public void OutdentSourceBlocks_LeavesSingleLineSourceUntouched()
    {
        var text =
            "\tpartition Dates = calculated\n" +
            "\t\tsource = Calendar(Date(2020, 1, 1), Date(2021, 12, 31))\n";

        Assert.Same(text, TomModelExporter.OutdentSourceBlocks(text));
    }

    [Fact]
    public void OutdentSourceBlocks_LeavesFencedExpressionUntouched()
    {
        var text =
            "\tpartition Sales = m\n" +
            "\t\tsource = ```\n" +
            "\t\t\t\tlet x = 1 in x\n" +
            "\t\t\t\t```\n";

        Assert.Same(text, TomModelExporter.OutdentSourceBlocks(text));
    }

    [Fact]
    public void OutdentSourceBlocks_LeavesCalculatedPartitionUntouched()
    {
        // Desktop indents DAX (calculated) source bodies two levels deep — same as the
        // serializer — so outdenting them would CREATE churn on calculation-group tables.
        var text =
            "\tpartition 'KPI Table' = calculated\n" +
            "\t\tmode: import\n" +
            "\t\tsource =\n" +
            "\t\t\t\t{\n" +
            "\t\t\t\t    (\"A\", NAMEOF([A]), 0)\n" +
            "\t\t\t\t}\n";

        Assert.Same(text, TomModelExporter.OutdentSourceBlocks(text));
    }

    [Fact]
    public void OutdentSourceBlocks_HandlesMultiplePartitions()
    {
        var input =
            "\tpartition A = m\n" +
            "\t\tsource =\n" +
            "\t\t\t\tlet a = 1 in a\n" +
            "\tpartition B = m\n" +
            "\t\tsource =\n" +
            "\t\t\t\tlet b = 2 in b\n";

        var result = TomModelExporter.OutdentSourceBlocks(input);
        Assert.Contains("\t\t\tlet a = 1 in a", result);
        Assert.Contains("\t\t\tlet b = 2 in b", result);
        Assert.DoesNotContain("\t\t\t\tlet", result);
    }

    [Fact]
    public void OutdentSourceBlocks_PreservesCrlfLineEndings()
    {
        var input = "\tpartition Sales = m\r\n\t\tsource =\r\n\t\t\t\tlet x = 1 in x\r\n";

        Assert.Equal(
            "\tpartition Sales = m\r\n\t\tsource =\r\n\t\t\tlet x = 1 in x\r\n",
            TomModelExporter.OutdentSourceBlocks(input));
    }

    [Fact]
    public async Task ExportTmdl_WritesSourceBlocksAtDesktopDepth_AndRoundTrips()
    {
        var db = new Database { Name = "M", Model = new Model { Name = "Model" } };
        var table = new Table { Name = "Sales" };
        const string expression = "let\n\tSource = #table({}, {})\nin\n\tSource";
        table.Partitions.Add(new Partition
        {
            Name = "Sales",
            Source = new MPartitionSource { Expression = expression }
        });
        db.Model.Tables.Add(table);

        var dir = Path.Combine(Path.GetTempPath(), $"tomix-indent-test-{Guid.NewGuid():N}");
        try
        {
            await TomModelExporter.ExportAsync(
                db, new ModelExportRequest(dir, "tmdl", Force: true, SupportingFiles: false), CancellationToken.None);

            var text = await File.ReadAllTextAsync(Path.Combine(dir, "tables", "Sales.tmdl"));
            Assert.Contains("\t\tsource =\n\t\t\tlet\n", text.ReplaceLineEndings("\n"));

            var roundTripped = TmdlSerializer.DeserializeDatabaseFromFolder(dir);
            var source = (MPartitionSource)roundTripped.Model.Tables["Sales"].Partitions["Sales"].Source;
            Assert.Equal(expression.ReplaceLineEndings("\n"), source.Expression.ReplaceLineEndings("\n"));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
