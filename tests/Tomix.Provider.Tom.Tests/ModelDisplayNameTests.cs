namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// The model display-name fallback chain: the TOM database name wins; a nameless database
/// (the norm for PBIP folders and hand-written TMDL) falls back to the sibling
/// <c>.platform</c> displayName, then to the source file/folder name (Fabric item suffixes
/// stripped), then to the caller's fallback, then to the "(unnamed)" sentinel. This value is
/// what every command prints as the model name, so the chain is pinned step by step.
/// </summary>
public sealed class ModelDisplayNameTests
{
    [Fact]
    public void Resolve_TomNameWins_OverPlatformAndPath()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, ".platform"),
            """{ "metadata": { "type": "SemanticModel", "displayName": "Platform Name" } }""");

        Assert.Equal("Tom Name", ModelDisplayName.Resolve("Tom Name", temp.Path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankTomName_UsesPlatformDisplayName(string? tomName)
    {
        using var temp = new TempDir();
        var modelDir = temp.CreateSubdirectory("Folder Name.SemanticModel");
        File.WriteAllText(Path.Combine(modelDir, ".platform"),
            """{ "metadata": { "type": "SemanticModel", "displayName": "Adventure Works" } }""");

        Assert.Equal("Adventure Works", ModelDisplayName.Resolve(tomName, modelDir));
    }

    [Fact]
    public void Resolve_MalformedPlatform_FallsThroughToFolderName()
    {
        using var temp = new TempDir();
        var modelDir = temp.CreateSubdirectory("My Model.SemanticModel");
        File.WriteAllText(Path.Combine(modelDir, ".platform"), "{ not json");

        Assert.Equal("My Model", ModelDisplayName.Resolve(null, modelDir));
    }

    [Fact]
    public void Resolve_PlatformWithoutDisplayName_FallsThroughToFolderName()
    {
        using var temp = new TempDir();
        var modelDir = temp.CreateSubdirectory("My Model.SemanticModel");
        File.WriteAllText(Path.Combine(modelDir, ".platform"),
            """{ "metadata": { "type": "SemanticModel" } }""");

        Assert.Equal("My Model", ModelDisplayName.Resolve(null, modelDir));
    }

    [Theory]
    [InlineData("My Model.SemanticModel", "My Model")]
    [InlineData("My Report.Report", "My Report")]
    [InlineData("basic-tmdl", "basic-tmdl")]
    public void Resolve_FolderSource_UsesFolderName_StrippingItemSuffixes(string folder, string expected)
    {
        using var temp = new TempDir();
        var modelDir = temp.CreateSubdirectory(folder);

        Assert.Equal(expected, ModelDisplayName.Resolve(null, modelDir));
    }

    [Fact]
    public void Resolve_FileSource_UsesFileNameWithoutExtension()
    {
        using var temp = new TempDir();
        var file = temp.WriteFile("Wide World Importers.bim", "{}");

        Assert.Equal("Wide World Importers", ModelDisplayName.Resolve(null, file));
    }

    [Fact]
    public void Resolve_FileSource_PlatformSibling_BeatsFileName()
    {
        using var temp = new TempDir();
        temp.WriteFile("model.bim", "{}");
        File.WriteAllText(Path.Combine(temp.Path, ".platform"),
            """{ "metadata": { "displayName": "Wide World Importers" } }""");

        Assert.Equal("Wide World Importers", ModelDisplayName.Resolve(null, temp.Combine("model.bim")));
    }

    [Fact]
    public void Resolve_DefinitionFolder_ReadsItemRootPlatform()
    {
        // PBIP layout: <Item>.SemanticModel/.platform sits one level above definition/.
        using var temp = new TempDir();
        var definition = temp.CreateSubdirectory("Adventure Works.SemanticModel", "definition");
        File.WriteAllText(Path.Combine(definition, "..", ".platform"),
            """{ "metadata": { "type": "SemanticModel", "displayName": "Adventure Works" } }""");

        Assert.Equal("Adventure Works", ModelDisplayName.Resolve(null, definition));
    }

    [Fact]
    public void Resolve_DefinitionFolder_WithoutPlatform_UsesItemFolderName()
    {
        using var temp = new TempDir();
        var definition = temp.CreateSubdirectory("My Item.SemanticModel", "definition");

        Assert.Equal("My Item", ModelDisplayName.Resolve(null, definition));
    }

    [Fact]
    public void Resolve_NoPath_UsesCallerFallback()
        => Assert.Equal("db-guid", ModelDisplayName.Resolve(null, sourcePath: null, fallback: "db-guid"));

    [Fact]
    public void Resolve_NothingAvailable_ReturnsUnnamedSentinel()
        => Assert.Equal("(unnamed)", ModelDisplayName.Resolve(null, sourcePath: null, fallback: null));
}
