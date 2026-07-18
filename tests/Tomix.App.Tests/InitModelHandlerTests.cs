using Tomix.App.Init;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;
using Tomix.Provider.Tom;

namespace Tomix.App.Tests;

/// <summary>
/// Behavioral tests for <see cref="InitModelHandler"/>. Scaffolds land in a per-test temp
/// directory, and the "openable" tests prove the scaffold is a valid model by opening it with
/// the real provider that owns the format (TMDL provider for tmdl/pbip, TOM file provider
/// for bim).
/// </summary>
public sealed class InitModelHandlerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"tomix-init-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // ---- Request validation failures -------------------------------------------------------

    [Fact]
    public void Handle_EmptyOutputPath_FailsWithOutputRequired()
    {
        var result = new InitModelHandler().Handle(NewRequest(outputPath: "   "));

        Assert.False(result.Success);
        Assert.Equal("TOMIX_INIT_OUTPUT_REQUIRED", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void Handle_UnsupportedSerialization_FailsWithUnsupportedSerialization()
    {
        var result = new InitModelHandler().Handle(
            NewRequest(outputPath: OutputPath("model"), serialization: "yaml"));

        Assert.False(result.Success);
        Assert.Equal("TOMIX_INIT_UNSUPPORTED_SERIALIZATION", result.Diagnostics[0].Code);
        Assert.Contains("yaml", result.Diagnostics[0].Message);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void Handle_UnsupportedCompatibilityMode_FailsWithUnsupportedCompatibilityMode()
    {
        var result = new InitModelHandler().Handle(
            NewRequest(outputPath: OutputPath("model"), compatibilityMode: "multidimensional"));

        Assert.False(result.Success);
        Assert.Equal("TOMIX_INIT_UNSUPPORTED_COMPATIBILITY_MODE", result.Diagnostics[0].Code);
        Assert.Equal(2, result.ExitCode);
    }

    // ---- TMDL scaffolding -------------------------------------------------------------------

    [Fact]
    public async Task Handle_TmdlDefaults_ScaffoldsModelOpenableByTmdlProvider()
    {
        var outputPath = OutputPath("MyModel");

        var result = new InitModelHandler().Handle(NewRequest(outputPath));

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var data = result.Data!;
        Assert.Equal(Path.GetFullPath(outputPath), data.Created);
        Assert.Equal("tmdl", data.Format);
        Assert.Equal("MyModel", data.Name);
        Assert.Equal(1702, data.CompatibilityLevel);
        Assert.Equal("PowerBI", data.CompatibilityMode);

        var database = File.ReadAllText(Path.Combine(outputPath, "database.tmdl"));
        Assert.Contains("database MyModel", database);
        Assert.Contains("compatibilityLevel: 1702", database);
        Assert.Contains("compatibilityMode: powerbi", database);
        Assert.Contains(
            "defaultPowerBIDataSourceVersion: powerBI_V3",
            File.ReadAllText(Path.Combine(outputPath, "model.tmdl")));

        var summary = await OpenAndSummarizeAsync(new TmdlModelProvider(), outputPath);
        Assert.Equal(0, summary.Tables);
    }

    [Fact]
    public async Task Handle_TmdlAnalysisServicesMode_DefaultsCompatibilityLevel1500()
    {
        var outputPath = OutputPath("AsModel");

        var result = new InitModelHandler().Handle(
            NewRequest(outputPath, compatibilityMode: "AnalysisServices"));

        Assert.True(result.Success);
        Assert.Equal(1500, result.Data!.CompatibilityLevel);
        Assert.Equal("AnalysisServices", result.Data.CompatibilityMode);

        var database = File.ReadAllText(Path.Combine(outputPath, "database.tmdl"));
        Assert.Contains("compatibilityLevel: 1500", database);
        Assert.Contains("compatibilityMode: analysisservices", database);
        Assert.DoesNotContain(
            "defaultPowerBIDataSourceVersion",
            File.ReadAllText(Path.Combine(outputPath, "model.tmdl")));

        var summary = await OpenAndSummarizeAsync(new TmdlModelProvider(), outputPath);
        Assert.Equal(1500, summary.CompatibilityLevel);
    }

    [Fact]
    public async Task Handle_TmdlNameWithSpaces_QuotesTmdlIdentifierAndStaysOpenable()
    {
        var outputPath = OutputPath("scaffold");

        var result = new InitModelHandler().Handle(
            NewRequest(outputPath, name: "My Sales Model", compatibilityLevel: 1605));

        Assert.True(result.Success);
        Assert.Equal("My Sales Model", result.Data!.Name);
        Assert.Equal(1605, result.Data.CompatibilityLevel);

        var database = File.ReadAllText(Path.Combine(outputPath, "database.tmdl"));
        Assert.Contains("database 'My Sales Model'", database);
        Assert.Contains("compatibilityLevel: 1605", database);

        var summary = await OpenAndSummarizeAsync(new TmdlModelProvider(), outputPath);
        Assert.Equal(0, summary.Tables);
    }

    [Fact]
    public void Handle_TmdlExistingDirectoryWithForce_ClearsPreviousContents()
    {
        var outputPath = OutputPath("reused");
        Directory.CreateDirectory(Path.Combine(outputPath, "old-sub"));
        File.WriteAllText(Path.Combine(outputPath, "stale.txt"), "old");
        File.WriteAllText(Path.Combine(outputPath, "old-sub", "leftover.tmdl"), "junk");

        var result = new InitModelHandler().Handle(NewRequest(outputPath, force: true));

        Assert.True(result.Success);
        Assert.False(File.Exists(Path.Combine(outputPath, "stale.txt")));
        Assert.False(Directory.Exists(Path.Combine(outputPath, "old-sub")));
        Assert.True(File.Exists(Path.Combine(outputPath, "database.tmdl")));
        Assert.True(File.Exists(Path.Combine(outputPath, "model.tmdl")));
    }

    // ---- BIM scaffolding ----------------------------------------------------------------------

    [Fact]
    public async Task Handle_BimIntoDirectory_ScaffoldsModelBimOpenableByTomFileProvider()
    {
        var outputPath = OutputPath("BimModel");

        var result = new InitModelHandler().Handle(NewRequest(outputPath, serialization: "bim"));

        Assert.True(result.Success);
        Assert.Equal("bim", result.Data!.Format);
        Assert.Equal(Path.GetFullPath(outputPath), result.Data.Created);

        var bimPath = Path.Combine(outputPath, "model.bim");
        Assert.True(File.Exists(bimPath));

        var provider = new TomFileModelProvider();
        var summary = await OpenAndSummarizeAsync(provider, bimPath);
        Assert.Equal("BimModel", summary.Name);
        Assert.Equal(1702, summary.CompatibilityLevel);
        Assert.Equal(0, summary.Tables);
    }

    [Fact]
    public void Handle_BimExplicitFilePath_CreatesThatFile()
    {
        var bimPath = OutputPath("explicit.bim");

        var result = new InitModelHandler().Handle(NewRequest(bimPath, serialization: "bim"));

        Assert.True(result.Success);
        Assert.Equal(Path.GetFullPath(bimPath), result.Data!.Created);
        Assert.True(File.Exists(bimPath));
    }

    [Fact]
    public void Handle_BimTargetExistsWithoutForce_FailsWithOutputExists()
    {
        var bimPath = OutputPath("taken.bim");
        Directory.CreateDirectory(_root);
        File.WriteAllText(bimPath, "{}");

        var result = new InitModelHandler().Handle(NewRequest(bimPath, serialization: "bim"));

        Assert.False(result.Success);
        Assert.Equal("TOMIX_INIT_OUTPUT_EXISTS", result.Diagnostics[0].Code);
        Assert.Contains(Path.GetFullPath(bimPath), result.Diagnostics[0].Message);
        Assert.Equal(2, result.ExitCode);
        // The pre-existing file is left untouched.
        Assert.Equal("{}", File.ReadAllText(bimPath));
    }

    [Fact]
    public void Handle_BimTargetExistsWithForce_Overwrites()
    {
        var bimPath = OutputPath("overwrite.bim");
        Directory.CreateDirectory(_root);
        File.WriteAllText(bimPath, "{}");

        var result = new InitModelHandler().Handle(
            NewRequest(bimPath, serialization: "bim", force: true));

        Assert.True(result.Success);
        Assert.Contains("compatibilityLevel", File.ReadAllText(bimPath));
    }

    // ---- PBIP scaffolding -----------------------------------------------------------------------

    [Fact]
    public async Task Handle_Pbip_ScaffoldsProjectOpenableViaPbipFile()
    {
        var outputPath = OutputPath("MyProject");

        var result = new InitModelHandler().Handle(NewRequest(outputPath, serialization: "pbip"));

        Assert.True(result.Success);
        Assert.Equal("pbip", result.Data!.Format);

        var pbipFile = Path.Combine(outputPath, "MyProject.pbip");
        var semanticModel = Path.Combine(outputPath, "MyProject.SemanticModel");
        var report = Path.Combine(outputPath, "MyProject.Report");
        Assert.True(File.Exists(pbipFile));
        Assert.True(File.Exists(Path.Combine(semanticModel, "definition", "database.tmdl")));
        Assert.True(File.Exists(Path.Combine(semanticModel, "definition", "model.tmdl")));
        Assert.True(File.Exists(Path.Combine(semanticModel, "definition.pbism")));
        Assert.True(File.Exists(Path.Combine(semanticModel, ".platform")));
        Assert.True(File.Exists(Path.Combine(report, "definition.pbir")));
        Assert.True(File.Exists(Path.Combine(report, "definition", "report.json")));
        Assert.True(File.Exists(Path.Combine(report, "definition", "pages", "pages.json")));

        // The .pbip entry point resolves to the semantic model's TMDL definition.
        var summary = await OpenAndSummarizeAsync(new TmdlModelProvider(), pbipFile);
        Assert.Equal(0, summary.Tables);
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    private string OutputPath(string name) => Path.Combine(_root, name);

    private static InitModelRequest NewRequest(
        string outputPath,
        string? name = null,
        string serialization = "",
        string compatibilityMode = "",
        int? compatibilityLevel = null,
        bool force = false)
        => new(outputPath, name, serialization, compatibilityMode, compatibilityLevel, force);

    private static async Task<ModelSummary> OpenAndSummarizeAsync(IModelProvider provider, string path)
    {
        var reference = new ModelReference(path);
        Assert.True(provider.CanOpen(reference), $"Provider should claim scaffolded model at {path}");

        await using var session = await provider.OpenAsync(reference, CancellationToken.None);
        return await session.GetSummaryAsync(CancellationToken.None);
    }
}
