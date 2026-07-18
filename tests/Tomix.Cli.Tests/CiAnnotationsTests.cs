using Tomix.Cli.Output;

namespace Tomix.Cli.Tests;

public class CiAnnotationsTests
{
    private static string Emit(string? ci, params CiAnnotation[] annotations)
    {
        using var writer = new StringWriter();
        CiAnnotations.Emit(ci, annotations, writer);
        return writer.ToString();
    }

    [Fact]
    public void Github_Maps_Error_And_Warning_Levels()
    {
        var output = Emit(
            "github",
            new CiAnnotation(IsError: true, "Rule A: Table 'Sales' [R1]"),
            new CiAnnotation(IsError: false, "Rule B: Column 'Qty' [R2]"));

        Assert.Equal(
            "::error::Rule A: Table 'Sales' [R1]" + Environment.NewLine +
            "::warning::Rule B: Column 'Qty' [R2]" + Environment.NewLine,
            output);
    }

    [Fact]
    public void Vsts_Maps_Levels_And_Fails_Task_When_Any_Error()
    {
        var output = Emit(
            "vsts",
            new CiAnnotation(IsError: false, "warn one"),
            new CiAnnotation(IsError: true, "err one"));

        Assert.Equal(
            "##vso[task.logissue type=warning;]warn one" + Environment.NewLine +
            "##vso[task.logissue type=error;]err one" + Environment.NewLine +
            "##vso[task.complete result=Failed;]Done." + Environment.NewLine,
            output);
    }

    [Fact]
    public void Vsts_Omits_Task_Complete_When_Warnings_Only()
    {
        var output = Emit("vsts", new CiAnnotation(IsError: false, "warn only"));

        Assert.Equal("##vso[task.logissue type=warning;]warn only" + Environment.NewLine, output);
        Assert.DoesNotContain("task.complete", output);
    }

    [Theory]
    [InlineData("GitHub")]
    [InlineData("VSTS")]
    public void Ci_Values_Are_Case_Insensitive(string ci)
        => Assert.NotEmpty(Emit(ci, new CiAnnotation(IsError: true, "msg")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("teamcity")]
    public void Unknown_Or_Blank_Ci_Is_A_NoOp(string? ci)
        => Assert.Equal("", Emit(ci, new CiAnnotation(IsError: true, "msg")));

    [Theory]
    [InlineData("github")]
    [InlineData("vsts")]
    public void Empty_Annotations_Emit_Nothing(string ci)
        => Assert.Equal("", Emit(ci));
}
