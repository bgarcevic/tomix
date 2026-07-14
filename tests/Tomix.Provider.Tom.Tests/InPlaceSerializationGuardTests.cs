using Tomix.Provider.Tom;

namespace Tomix.Provider.Tom.Tests;

/// <summary>
/// An in-place mutation save must keep the source serialization: exporting a TMDL folder
/// "in place" as bim wrote a stray definition.bim next to the real definition and left the
/// model untouched while the CLI reported "Saved" (live-model mv QA finding).
/// </summary>
public sealed class InPlaceSerializationGuardTests
{
    [Theory]
    [InlineData("bim", "tmdl")]
    [InlineData("tmsl", "tmdl")]
    [InlineData("tmdl", "bim")]
    public void InPlace_MismatchedFormat_Throws(string requested, string sourceFormat)
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => InPlaceSerializationGuard.Resolve(inPlace: true, requested, sourceFormat));

        Assert.Contains("--save-to", ex.Message);
    }

    [Theory]
    [InlineData("", "tmdl", "tmdl")]
    [InlineData("auto", "tmdl", "tmdl")]
    [InlineData("auto", "bim", "bim")]
    [InlineData("TMDL", "tmdl", "tmdl")]
    [InlineData("tmsl", "bim", "bim")]
    public void InPlace_MatchingOrDefaultFormat_ResolvesToSource(string requested, string sourceFormat, string expected)
    {
        Assert.Equal(expected, InPlaceSerializationGuard.Resolve(inPlace: true, requested, sourceFormat));
    }

    [Theory]
    [InlineData("bim", "tmdl", "bim")]
    [InlineData("tmsl", "tmdl", "bim")]
    [InlineData("", "tmdl", "tmdl")]
    public void SaveTo_AnyFormatAllowed(string requested, string sourceFormat, string expected)
    {
        Assert.Equal(expected, InPlaceSerializationGuard.Resolve(inPlace: false, requested, sourceFormat));
    }
}
