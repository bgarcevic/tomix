using Tomix.App.Mutations;

namespace Tomix.App.Tests;

/// <summary>
/// <see cref="MutationLifecycle.ResolveMode"/> mutual-exclusion matrix — especially
/// <c>--revert --save-to</c>, which used to silently drop the save target.
/// </summary>
public sealed class MutationLifecycleModeTests
{
    private static MutationOptions Options(
        bool save = false, string? saveTo = null, bool stage = false, bool revert = false)
        => new(save, saveTo, stage, revert, Serialization: "", Force: false);

    [Fact]
    public void SaveAndStage_Conflict()
    {
        var error = MutationLifecycle.ResolveMode(Options(save: true, stage: true), out _);

        Assert.NotNull(error);
        Assert.Equal("TOMIX_STAGE_SAVE_CONFLICT", error.Code);
        Assert.Equal(2, error.ExitCode);
    }

    [Theory]
    [InlineData(true, null, false)]
    [InlineData(false, null, true)]
    [InlineData(false, "out/path", false)]
    public void RevertWithPersistenceOption_Conflict(bool save, string? saveTo, bool stage)
    {
        var error = MutationLifecycle.ResolveMode(
            Options(save: save, saveTo: saveTo, stage: stage, revert: true), out _);

        Assert.NotNull(error);
        Assert.Equal("TOMIX_STAGE_OPTIONS_CONFLICT", error.Code);
        Assert.Equal(2, error.ExitCode);
        Assert.Contains("--save-to", error.Message);
    }

    [Fact]
    public void RevertAlone_ResolvesRevert()
    {
        var error = MutationLifecycle.ResolveMode(Options(revert: true), out var mode);

        Assert.Null(error);
        Assert.Equal(MutationMode.Revert, mode);
    }

    [Fact]
    public void SaveToAlone_ResolvesSave()
    {
        var error = MutationLifecycle.ResolveMode(Options(saveTo: "out/path"), out var mode);

        Assert.Null(error);
        Assert.Equal(MutationMode.Save, mode);
    }

    [Fact]
    public void NoOptions_ResolvesNone()
    {
        var error = MutationLifecycle.ResolveMode(Options(), out var mode);

        Assert.Null(error);
        Assert.Equal(MutationMode.None, mode);
    }
}
