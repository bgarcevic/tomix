using Dax.Vpax.Tools;
using Tomix.Core.Vertipaq;

namespace Tomix.Provider.Vpax.Tests;

public class VpaxRoundTripTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("tomix-vpax-tests-").FullName;

    private readonly VpaxVertipaqAnalyzer _analyzer = new(tokenProvider: null, version: "test");

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Import_reads_back_an_exported_package()
    {
        var path = Path.Combine(_directory, "model.vpax");
        VpaxTools.ExportVpax(path, TestDaxModelBuilder.Build());

        var stats = await _analyzer.ImportAsync(path, CancellationToken.None);

        Assert.Equal("TestModel", stats.ModelName);
        Assert.Equal(2, stats.TableCount);
        Assert.Equal(3, stats.ColumnCount);
        Assert.Equal(1000, stats.Columns.Single(c => c.ColumnName == "Amount").TotalSize);
        Assert.Single(stats.Relationships);
    }

    [Fact]
    public async Task Import_missing_file_throws_read_failure()
    {
        var ex = await Assert.ThrowsAsync<VertipaqAnalysisException>(
            () => _analyzer.ImportAsync(Path.Combine(_directory, "missing.vpax"), CancellationToken.None));

        Assert.Equal(VertipaqAnalysisKind.VpaxReadFailed, ex.Kind);
    }

    [Fact]
    public async Task Import_invalid_file_throws_read_failure()
    {
        var path = Path.Combine(_directory, "invalid.vpax");
        await File.WriteAllTextAsync(path, "not a vpax package");

        var ex = await Assert.ThrowsAsync<VertipaqAnalysisException>(
            () => _analyzer.ImportAsync(path, CancellationToken.None));

        Assert.Equal(VertipaqAnalysisKind.VpaxReadFailed, ex.Kind);
    }

    [Fact]
    public async Task Obfuscated_export_writes_dictionary_and_hides_names()
    {
        var path = Path.Combine(_directory, "obfuscated.vpax");

        var dictionaryPath = VpaxVertipaqAnalyzer.WriteObfuscated(TestDaxModelBuilder.Build(), path);

        Assert.Equal(Path.Combine(_directory, "obfuscated.dict"), dictionaryPath);
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(dictionaryPath));

        var stats = await _analyzer.ImportAsync(path, CancellationToken.None);
        Assert.Equal(2, stats.TableCount);
        Assert.DoesNotContain(stats.Tables, t => t.TableName is "Sales" or "Product");

        // Sizes survive obfuscation; only names/expressions are rewritten.
        Assert.Equal(1300, stats.Tables.Max(t => t.ColumnsTotalSize));
    }
}
