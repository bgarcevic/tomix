using Tomix.App.Format;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;
using Tomix.Tests.Support;

namespace Tomix.Cli.Tests;

/// <summary>
/// Corpus check: runs the offline formatter over every DAX expression in the checked-in sample
/// models — including the realistic PBIP fixtures with calculation items, calculated columns,
/// and KPIs. Real-world expressions must format without the token-signature safety check
/// declining, and formatting must be idempotent: formatting an already formatted expression
/// leaves it unchanged.
/// </summary>
public sealed class OfflineDaxFormatterCorpusTests
{
    public static TheoryData<string> SampleModels()
    {
        var data = new TheoryData<string> { SampleModel.DefaultName };
        foreach (var directory in Directory.EnumerateDirectories(RepoPaths.Samples, "*.SemanticModel"))
            data.Add(Path.GetFileName(directory));
        return data;
    }

    [Theory]
    [MemberData(nameof(SampleModels))]
    public async Task SampleModel_DaxExpressions_FormatOfflineAndAreIdempotent(string name)
    {
        var provider = new TmdlModelProvider();
        var reference = new ModelReference(SampleModel.Locate(name));
        Assert.True(provider.CanOpen(reference), $"The TMDL provider cannot open sample: {name}");

        await using var session = await provider.OpenAsync(reference, CancellationToken.None);
        var snapshot = await session.GetSnapshotAsync(CancellationToken.None);
        var client = new OfflineDaxFormatterClient();

        var checkedCount = 0;
        foreach (var (path, kind, expression) in DaxExpressions(snapshot.Objects))
        {
            var first = await client.FormatAsync(
                new ExpressionFormatRequest(expression, "dax", Long: false),
                CancellationToken.None);
            Assert.True(
                first.Success,
                $"Offline formatting declined {path} ({kind}): {string.Join("; ", first.Errors)}");

            var second = await client.FormatAsync(
                new ExpressionFormatRequest(first.Formatted, "dax", Long: false),
                CancellationToken.None);
            Assert.True(second.Success, $"Reformatting declined {path} ({kind}).");
            Assert.Equal(first.Formatted, second.Formatted);
            checkedCount++;
        }

        Assert.True(
            checkedCount >= 3,
            $"Expected the corpus to exercise real expressions, but only {checkedCount} were checked in {name}.");
    }

    /// <summary>
    /// Every expression-bearing object except partitions and shared expressions, which carry M
    /// rather than DAX. Walks the public snapshot tree, mirroring what <c>tx format</c>'s default
    /// sweep targets plus the other DAX-bearing kinds (calculated columns, calculation items).
    /// </summary>
    private static IEnumerable<(string Path, ModelObjectKind Kind, string Expression)> DaxExpressions(
        IEnumerable<ModelObject> objects)
    {
        foreach (var obj in objects)
        {
            if (!string.IsNullOrWhiteSpace(obj.Expression) &&
                obj.Kind is not (ModelObjectKind.Partition or ModelObjectKind.Expression))
                yield return (obj.Path, obj.Kind, obj.Expression!);

            foreach (var child in DaxExpressions(obj.Children))
                yield return child;
        }
    }
}
