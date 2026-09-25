using System.Text;
using Tomix.Core.Models;

namespace Tomix.Provider.Tmdl.Tests;

/// <summary>
/// Repository hygiene for in-place TMDL saves (#224, #201): a save rewrites only the files whose
/// serialized content changed, so a small edit produces a small git diff. Untouched files must stay
/// byte-identical, including their line endings (a CRLF checkout under <c>core.autocrlf</c> stays
/// CRLF) and their M partition source indentation.
/// </summary>
public sealed class TmdlModelSessionSaveHygieneTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    public void Dispose() => _tempDir.Dispose();

    /// <summary>
    /// Every sample whose TMDL is in serializer-canonical form. Excluded: <c>qa-fixture</c>,
    /// <c>qa-fixture-variant</c>, and <c>refresh-policy-qa</c> are hand-authored with non-canonical
    /// property order, <c>ref culture</c> keywords, and expression indentation, which the serializer
    /// normalizes on the first save (their later saves are stable).
    /// </summary>
    public static TheoryData<string> Samples() =>
    [
        "basic-tmdl",
        "deploy-qa",
        Path.Combine("AdventureWorks Sales.SemanticModel", "definition"),
        Path.Combine("Artificial Intelligence Sample.SemanticModel", "definition"),
        Path.Combine("Competitive Marketing Analysis.SemanticModel", "definition"),
        Path.Combine("Corporate Spend.SemanticModel", "definition"),
        Path.Combine("Employee Hiring and History.SemanticModel", "definition"),
        Path.Combine("Regional Sales Sample.SemanticModel", "definition"),
        Path.Combine("Revenue Opportunities.SemanticModel", "definition"),
        Path.Combine("Store Sales.SemanticModel", "definition"),
    ];

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task SaveAsync_Unmodified_LeavesEveryFileByteIdentical(string sample)
    {
        var modelPath = SampleModel.CopyTo(_tempDir, "model", sample);
        var before = Snapshot(modelPath);

        await using (var session = new TmdlModelSession(modelPath))
            await session.SaveAsync(outputPath: null, "tmdl", overwrite: false, CancellationToken.None);

        AssertUnchanged(before, Snapshot(modelPath), except: []);
    }

    [Theory]
    [InlineData("qa-fixture")]
    [InlineData("qa-fixture-variant")]
    [InlineData("refresh-policy-qa")]
    public async Task SaveAsync_Twice_SecondSaveIsNoOp_AndKeepsNonTmdlFiles(string sample)
    {
        var modelPath = SampleModel.CopyTo(_tempDir, "model", sample);
        var extras = Directory.GetFiles(modelPath, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".tmdl", StringComparison.Ordinal))
            .ToList();

        await using (var session = new TmdlModelSession(modelPath))
            await session.SaveAsync(outputPath: null, "tmdl", overwrite: false, CancellationToken.None);
        var first = Snapshot(modelPath);

        await using (var session = new TmdlModelSession(modelPath))
            await session.SaveAsync(outputPath: null, "tmdl", overwrite: false, CancellationToken.None);

        AssertUnchanged(first, Snapshot(modelPath), except: []);
        Assert.All(extras, f => Assert.True(File.Exists(f), $"non-TMDL file deleted: {f}"));
    }

    [Fact]
    public async Task SetProperty_OnePropertyEdit_RewritesOnlyTheDefiningFile()
    {
        // The #201 repro: AdventureWorks' M source blocks sit two levels below `source =`; a
        // description edit on Sales must not re-indent Sales' partition or touch any other table.
        var modelPath = SampleModel.CopyTo(
            _tempDir, "model", Path.Combine("AdventureWorks Sales.SemanticModel", "definition"));
        var before = Snapshot(modelPath);

        await using (var session = new TmdlModelSession(modelPath))
        {
            Assert.True(session.SetProperty(new ModelObjectSetRequest(
                "Sales/Cost", [new ModelPropertyAssignment("description", "QA probe")], Type: null)).Changed);
            await session.SaveAsync(outputPath: null, "tmdl", overwrite: false, CancellationToken.None);
        }

        var after = Snapshot(modelPath);
        var sales = Path.Combine("tables", "Sales.tmdl");
        AssertUnchanged(before, after, except: [sales]);

        var oldLines = before[sales].ReplaceLineEndings("\n").Split('\n');
        var newLines = after[sales].ReplaceLineEndings("\n").Split('\n');
        Assert.Equal(oldLines.Length, newLines.Length);
        var changedLines = newLines.Where((line, i) => line != oldLines[i]).ToList();
        Assert.Equal(["\t/// QA probe"], changedLines);
    }

    [Fact]
    public async Task RenameMeasure_TouchesOnlyTheDefiningFile()
    {
        var modelPath = SampleModel.CopyTo(_tempDir, "model");
        var before = Snapshot(modelPath);
        var timestamps = before.Keys.ToDictionary(
            k => k, k => File.GetLastWriteTimeUtc(Path.Combine(modelPath, k)));

        await using (var session = new TmdlModelSession(modelPath))
        {
            Assert.True(session.SetProperty(new ModelObjectSetRequest(
                "'Sales'[Total Sales]", [new ModelPropertyAssignment("name", "Revenue")], Type: null)).Changed);
            await session.SaveAsync(outputPath: null, "tmdl", overwrite: false, CancellationToken.None);
        }

        var after = Snapshot(modelPath);
        var sales = Path.Combine("tables", "Sales.tmdl");
        AssertUnchanged(before, after, except: [sales]);
        foreach (var file in before.Keys.Where(k => k != sales))
            Assert.Equal(timestamps[file], File.GetLastWriteTimeUtc(Path.Combine(modelPath, file)));
    }

    [Fact]
    public async Task SaveAsync_CrlfCheckout_KeepsCrlfInRewrittenFiles()
    {
        var modelPath = SampleModel.CopyTo(_tempDir, "model");
        foreach (var file in Directory.GetFiles(modelPath, "*.tmdl", SearchOption.AllDirectories))
            File.WriteAllText(file, File.ReadAllText(file).ReplaceLineEndings("\n").Replace("\n", "\r\n"));
        var before = Snapshot(modelPath);

        await using (var session = new TmdlModelSession(modelPath))
        {
            session.SetProperty(new ModelObjectSetRequest(
                "'Sales'[Total Sales]", [new ModelPropertyAssignment("description", "d")], Type: null));
            await session.SaveAsync(outputPath: null, "tmdl", overwrite: false, CancellationToken.None);
        }

        var after = Snapshot(modelPath);
        var sales = Path.Combine("tables", "Sales.tmdl");
        AssertUnchanged(before, after, except: [sales]);
        Assert.NotEqual(before[sales], after[sales]);
        Assert.DoesNotContain("\n", after[sales].Replace("\r\n", ""));
    }

    [Fact]
    public async Task RemoveTable_DeletesItsFile()
    {
        var modelPath = SampleModel.CopyTo(_tempDir, "model");
        var products = Path.Combine(modelPath, "tables", "Products.tmdl");
        Assert.True(File.Exists(products));

        await using (var session = new TmdlModelSession(modelPath))
        {
            Assert.True(session.RemoveObject(
                new ModelObjectRemoveRequest("Products", ModelObjectKind.Table, IfExists: false)).Changed);
            await session.SaveAsync(outputPath: null, "tmdl", overwrite: false, CancellationToken.None);
        }

        Assert.False(File.Exists(products));
        Assert.True(File.Exists(Path.Combine(modelPath, "tables", "Sales.tmdl")));
    }

    [Fact]
    public async Task RenameTable_CaseOnly_WritesFileUnderNewCasing()
    {
        var modelPath = SampleModel.CopyTo(_tempDir, "model");

        await using (var session = new TmdlModelSession(modelPath))
        {
            Assert.True(session.SetProperty(new ModelObjectSetRequest(
                "Sales", [new ModelPropertyAssignment("name", "SALES")], Type: null)).Changed);
            await session.SaveAsync(outputPath: null, "tmdl", overwrite: false, CancellationToken.None);
        }

        var names = Directory.GetFiles(Path.Combine(modelPath, "tables")).Select(Path.GetFileName).ToList();
        Assert.Contains("SALES.tmdl", names);
        Assert.DoesNotContain("Sales.tmdl", names);

        await using var reopened = new TmdlModelSession(modelPath);
        var snapshot = await reopened.GetSnapshotAsync(CancellationToken.None);
        Assert.Contains(snapshot.Objects, o => o.Kind == ModelObjectKind.Table && o.Name == "SALES");
    }

    [Fact]
    public async Task SaveAsync_ToNewFolder_IsDeterministic_LfAndBomLess()
    {
        var modelPath = SampleModel.CopyTo(_tempDir, "model");
        var a = _tempDir.Combine("a");
        var b = _tempDir.Combine("b");

        await using (var session = new TmdlModelSession(modelPath))
        {
            await session.SaveAsync(a, "tmdl", overwrite: false, CancellationToken.None);
            await session.SaveAsync(b, "tmdl", overwrite: false, CancellationToken.None);
        }

        var first = Snapshot(a);
        Assert.Equal(first, Snapshot(b));
        foreach (var (file, text) in first)
        {
            var bytes = File.ReadAllBytes(Path.Combine(a, file));
            Assert.NotEqual(0xEF, bytes[0]);          // no UTF-8 BOM
            Assert.DoesNotContain((byte)'\r', bytes); // LF on every OS
            Assert.EndsWith("\n", text);
        }
    }

    private static Dictionary<string, string> Snapshot(string root)
        => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                f => Path.GetRelativePath(root, f),
                f => Encoding.Latin1.GetString(File.ReadAllBytes(f)));

    private static void AssertUnchanged(
        Dictionary<string, string> before,
        Dictionary<string, string> after,
        IReadOnlyCollection<string> except)
    {
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        var changed = before.Keys
            .Where(k => !except.Contains(k) && before[k] != after[k])
            .ToList();
        Assert.True(changed.Count == 0, "Unexpectedly rewritten: " + string.Join(", ", changed));
    }
}
