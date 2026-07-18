using Tomix.App.Config;
using Tomix.Core.Configuration;

namespace Tomix.App.Tests;

public sealed class ConfigHandlerTests
{
    private static ConfigHandler NewHandler(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"tomix-config-test-{Guid.NewGuid():N}.json");
        return new ConfigHandler(new TomixConfigStore(path));
    }

    [Fact]
    public void Init_NoExistingFile_CreatesEmptyConfig()
    {
        var handler = NewHandler(out var path);
        try
        {
            var result = handler.Init(force: false);

            Assert.True(result.Success);
            Assert.True(result.Data!.Created);
            Assert.Equal(path, result.Data.Path);
            Assert.True(File.Exists(path));
            Assert.Empty(new TomixConfigStore(path).Load());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Init_ExistingFileWithoutForce_LeavesValuesIntact()
    {
        var handler = NewHandler(out var path);
        try
        {
            handler.Set(ConfigKeys.NoColor, "true");

            var result = handler.Init(force: false);

            Assert.True(result.Success);
            Assert.False(result.Data!.Created);
            Assert.Equal("true", new TomixConfigStore(path).Load()[ConfigKeys.NoColor]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Init_ExistingFileWithForce_ResetsToEmptyConfig()
    {
        var handler = NewHandler(out var path);
        try
        {
            handler.Set(ConfigKeys.NoColor, "true");

            var result = handler.Init(force: true);

            Assert.True(result.Success);
            Assert.True(result.Data!.Created);
            Assert.Empty(new TomixConfigStore(path).Load());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_CorruptJson_ThrowsActionableError()
    {
        NewHandler(out var path);
        try
        {
            File.WriteAllText(path, "{ not json");

            var ex = Assert.Throws<InvalidOperationException>(() => new TomixConfigStore(path).Load());

            Assert.Contains(path, ex.Message);
            Assert.Contains("tx config set", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Set_ThenGet_RoundTripsValue()
    {
        var handler = NewHandler(out var path);
        try
        {
            var set = handler.Set(ConfigKeys.DefaultFormat, "json");
            Assert.True(set.Success);

            var get = handler.Get(ConfigKeys.DefaultFormat);
            Assert.True(get.Success);
            Assert.Equal("json", get.Data!.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Set_UnknownKey_FailsWithExitCode2()
    {
        var handler = NewHandler(out var path);
        try
        {
            var result = handler.Set("bogusKey", "x");

            Assert.False(result.Success);
            Assert.Equal(2, result.ExitCode);
            Assert.Equal("TOMIX_CONFIG_UNKNOWN_KEY", result.Diagnostics[0].Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Set_InvalidValue_FailsWithExitCode2()
    {
        var handler = NewHandler(out var path);
        try
        {
            var result = handler.Set(ConfigKeys.DefaultFormat, "xml");

            Assert.False(result.Success);
            Assert.Equal(2, result.ExitCode);
            Assert.Equal("TOMIX_CONFIG_INVALID_VALUE", result.Diagnostics[0].Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Get_UnknownKey_FailsWithExitCode2()
    {
        var handler = NewHandler(out var path);

        var result = handler.Get("bogusKey");

        Assert.False(result.Success);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void Get_KnownButUnsetKey_ReturnsNullValue()
    {
        var handler = NewHandler(out var path);

        var result = handler.Get(ConfigKeys.ActiveProfile);

        Assert.True(result.Success);
        Assert.Null(result.Data!.Value);
    }

    [Fact]
    public void List_ReturnsStoredValues()
    {
        var handler = NewHandler(out var path);
        try
        {
            handler.Set(ConfigKeys.NoColor, "true");
            handler.Set(ConfigKeys.Telemetry, "false");

            var list = handler.List();

            Assert.True(list.Success);
            Assert.Equal(2, list.Data!.Values.Count);
            Assert.Equal("true", list.Data.Values[ConfigKeys.NoColor]);
            Assert.Equal("false", list.Data.Values[ConfigKeys.Telemetry]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
