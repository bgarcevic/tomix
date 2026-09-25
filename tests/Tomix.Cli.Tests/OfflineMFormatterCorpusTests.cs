using Tomix.App.Dax;
using Tomix.App.Format;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;
using Tomix.Tests.Support;

namespace Tomix.Cli.Tests;

/// <summary>
/// Corpus check: runs the offline M formatter over every partition and shared expression in the
/// checked-in sample models (calculated-table partitions carry DAX and are skipped). Real-world
/// M must format without error, and formatting must be idempotent: formatting an already
/// formatted expression leaves it unchanged, so
/// <c>tx format</c> reports it as <c>unchanged</c> and skips the write.
/// </summary>
public sealed class OfflineMFormatterCorpusTests
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
    public async Task SampleModel_MExpressions_FormatOfflineAndAreIdempotent(string name)
    {
        var provider = new TmdlModelProvider();
        var reference = new ModelReference(SampleModel.Locate(name));
        Assert.True(provider.CanOpen(reference), $"The TMDL provider cannot open sample: {name}");

        await using var session = await provider.OpenAsync(reference, CancellationToken.None);
        var snapshot = await session.GetSnapshotAsync(CancellationToken.None);
        var client = new OfflineMFormatterClient();

        var checkedCount = 0;
        foreach (var (path, kind, expression) in MExpressions(snapshot.Objects))
        {
            foreach (var isLong in new[] { false, true })
            {
                var first = await client.FormatAsync(
                    new ExpressionFormatRequest(expression, FormatterLanguages.PowerQuery, isLong),
                    CancellationToken.None);
                Assert.True(
                    first.Success,
                    $"Offline M formatting failed for {path} ({kind}, long: {isLong}): {string.Join("; ", first.Errors)}");

                var second = await client.FormatAsync(
                    new ExpressionFormatRequest(first.Formatted, FormatterLanguages.PowerQuery, isLong),
                    CancellationToken.None);
                Assert.True(second.Success, $"Reformatting failed for {path} ({kind}, long: {isLong}).");
                Assert.Equal(first.Formatted, second.Formatted);
            }

            checkedCount++;
        }

        Assert.True(
            checkedCount >= 1,
            $"Expected the corpus to exercise real M expressions, but none were checked in {name}.");
    }

    /// <summary>
    /// Partitions and shared expressions, minus calculated-table partitions (DAX): the objects
    /// <c>tx format</c> treats as M.
    /// </summary>
    private static IEnumerable<(string Path, ModelObjectKind Kind, string Expression)> MExpressions(
        IEnumerable<ModelObject> objects)
    {
        foreach (var obj in objects)
        {
            if (!string.IsNullOrWhiteSpace(obj.Expression) &&
                obj.Kind is ModelObjectKind.Partition or ModelObjectKind.Expression &&
                !DaxExpressions.IsDaxExpression(obj.Kind, obj.Detail))
                yield return (obj.Path, obj.Kind, obj.Expression!);

            foreach (var child in MExpressions(obj.Children))
                yield return child;
        }
    }
}
