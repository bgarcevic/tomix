using Mdl.App.Config;
using Mdl.Core.Configuration;

namespace Mdl.App.Tests;

public sealed class ConfigHandlerTests
{
    private static ConfigHandler NewHandler(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"mdl-config-test-{Guid.NewGuid():N}.json");
        return new ConfigHandler(new MdlConfigStore(path));
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
            Assert.Equal("MDL_CONFIG_UNKNOWN_KEY", result.Diagnostics[0].Code);
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
            Assert.Equal("MDL_CONFIG_INVALID_VALUE", result.Diagnostics[0].Code);
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
