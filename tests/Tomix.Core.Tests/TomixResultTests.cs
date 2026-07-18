using Tomix.Core.Diagnostics;
using Tomix.Core.Results;

namespace Tomix.Core.Tests;

public sealed class TomixResultTests
{
    [Fact]
    public void Ok_DefaultsToExitZero_WithNoDiagnostics()
    {
        var result = TomixResult<string>.Ok("data");

        Assert.True(result.Success);
        Assert.Equal("data", result.Data);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void Ok_CanCarryANonZeroExitCode()
    {
        // Commands like doctor render their report while signalling failure.
        var result = TomixResult<string>.Ok("report", exitCode: 1);

        Assert.True(result.Success);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public void Fail_DefaultsToExitOne_WithOneErrorDiagnostic()
    {
        var result = TomixResult<string>.Fail("TOMIX_TEST_FAILED", "it broke", hint: "fix it");

        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Equal(1, result.ExitCode);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("TOMIX_TEST_FAILED", diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("it broke", diagnostic.Message);
        Assert.Equal("fix it", diagnostic.Hint);
    }

    [Fact]
    public void Fail_CanOverrideTheExitCode()
    {
        var result = TomixResult<string>.Fail("TOMIX_TEST_USAGE", "bad flag", exitCode: 2);

        Assert.Equal(2, result.ExitCode);
    }
}
