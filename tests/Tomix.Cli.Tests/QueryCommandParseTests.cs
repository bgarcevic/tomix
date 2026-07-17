using System.CommandLine;
using Tomix.Cli.Commands;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>
/// Parse-time and input-resolution validation for <c>tx query</c>: bad --limit values fail
/// before any connection is opened, -q/--file conflicts and missing files produce their
/// dedicated diagnostics, and --param tokens split on the first '='.
/// </summary>
public sealed class QueryCommandParseTests
{
    private static ParseResult Parse(params string[] args)
    {
        var root = new RootCommand("test");
        foreach (var option in GlobalOptions.All())
            root.Options.Add(option);
        IReadOnlyList<IModelProvider> providers = [];
        root.Subcommands.Add(new QueryCommand(providers).Build());
        return root.Parse(args);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    public void Query_LimitBelowOne_FailsAtParseTime(string limit)
    {
        var result = Parse("query", "-q", "EVALUATE x", "--limit", limit);

        Assert.Contains(result.Errors, e => e.Message.Contains("--limit must be at least 1"));
    }

    [Fact]
    public void Query_AliasesAndRepeatableParams_Bind()
    {
        var result = Parse("query", "-q", "EVALUATE x", "-o", "out.csv", "--param", "a=1", "--param", "b=2");

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ResolveQueryInput_BothQueryAndFile_ReturnsConflict()
    {
        var (query, error) = QueryCommand.ResolveQueryInput("EVALUATE x", "query.dax");

        Assert.Null(query);
        Assert.Equal("TOMIX_QUERY_INPUT_CONFLICT", error!.Code);
    }

    [Fact]
    public void ResolveQueryInput_MissingFile_ReturnsFileNotFound()
    {
        var (query, error) = QueryCommand.ResolveQueryInput(null, Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.dax"));

        Assert.Null(query);
        Assert.Equal("TOMIX_QUERY_FILE_NOT_FOUND", error!.Code);
    }

    [Fact]
    public void ResolveQueryInput_File_ReadsContent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"query-{Guid.NewGuid():N}.dax");
        File.WriteAllText(path, "EVALUATE 'Sales'");
        try
        {
            var (query, error) = QueryCommand.ResolveQueryInput(null, path);

            Assert.Null(error);
            Assert.Equal("EVALUATE 'Sales'", query);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ResolveQueryInput_InlineQuery_PassesThrough()
    {
        var (query, error) = QueryCommand.ResolveQueryInput("EVALUATE 'Sales'", null);

        Assert.Null(error);
        Assert.Equal("EVALUATE 'Sales'", query);
    }

    [Fact]
    public void ParseParams_SplitsOnFirstEqualsAndStripsAt()
    {
        var parameters = QueryCommand.ParseParams(["@color=Red", "expr=a=b"], out var bad);

        Assert.Null(bad);
        Assert.Equal("Red", parameters!["color"]);
        Assert.Equal("a=b", parameters["expr"]);
    }

    [Fact]
    public void ParseParams_EmptyInput_YieldsEmptyDictionary()
    {
        var parameters = QueryCommand.ParseParams(null, out var bad);

        Assert.Null(bad);
        Assert.Empty(parameters!);
    }

    [Theory]
    [InlineData("noequals")]
    [InlineData("=value")]
    [InlineData("@=value")]
    public void ParseParams_MalformedToken_ReturnsNullWithBadValue(string token)
    {
        var parameters = QueryCommand.ParseParams([token], out var bad);

        Assert.Null(parameters);
        Assert.Equal(token, bad);
    }

    [Theory]
    [InlineData("out.json", null, OutputFormats.Json)]
    [InlineData("out.JSON", "auto", OutputFormats.Json)]
    [InlineData("out.csv", null, OutputFormats.Csv)]
    [InlineData("out.txt", null, OutputFormats.Csv)]
    [InlineData("out.csv", "json", OutputFormats.Json)]
    [InlineData("out.json", "csv", OutputFormats.Csv)]
    public void ResolveOutputFileFormat_PicksExplicitFormatThenExtension(string file, string? rawFormat, string expected)
        => Assert.Equal(expected, QueryCommand.ResolveOutputFileFormat(file, rawFormat));

    [Fact]
    public void ResolveOutputFileFormat_ExplicitText_IsRejected()
        => Assert.Null(QueryCommand.ResolveOutputFileFormat("out.csv", "text"));
}
