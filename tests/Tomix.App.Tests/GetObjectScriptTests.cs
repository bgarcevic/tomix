using Tomix.App.Get;
using Tomix.App.Tests.Support;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

/// <summary>
/// <c>get --output-format tmdl/bim/tmsl</c> (#243): the handler hands the resolved object to the
/// session's serializer, and refuses rather than fall back to a lossy fragment.
/// </summary>
public sealed class GetObjectScriptTests
{
    [Theory]
    [InlineData(ObjectScriptFormat.Tmdl)]
    [InlineData(ObjectScriptFormat.Json)]
    public async Task Script_ComesFromTheSession_ForTheResolvedObject(ObjectScriptFormat format)
    {
        var session = new ScriptSession();

        // A DAX-form path: the session must get the snapshot path, not what was typed.
        var result = await Get(session, "'Sales'[Base]", format);

        Assert.True(result.Success);
        Assert.Equal($"{format}:Sales/Base:Measure", result.Data!.Object!.Script);
    }

    [Fact]
    public async Task ModelRoot_IsScriptedAsTheModel()
    {
        var result = await Get(new ScriptSession(), ".", ObjectScriptFormat.Tmdl);

        Assert.Equal("Tmdl:.:Model", result.Data!.Object!.Script);
    }

    [Fact]
    public async Task NoScriptRequested_LeavesTheSessionAlone()
    {
        var session = new ScriptSession();

        var result = await Get(session, "Sales/Base", script: null);

        Assert.Null(result.Data!.Object!.Script);
        Assert.Equal(0, session.Calls);
    }

    /// <summary>A projected property prints as a scalar in any format, so nothing is scripted.</summary>
    [Fact]
    public async Task Query_WinsOverTheScript()
    {
        var session = new ScriptSession();

        var result = await Get(session, "Sales/Base", ObjectScriptFormat.Tmdl, query: "expression");

        Assert.Null(result.Data!.Object!.Script);
        Assert.Equal(0, session.Calls);
    }

    [Fact]
    public async Task SessionWithoutASerializer_FailsInsteadOfFallingBack()
    {
        var result = await Get(new MutationStubs.SnapshotSession(MutationStubs.BaseAndDerived()), "Sales/Base", ObjectScriptFormat.Tmdl);

        Assert.False(result.Success);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("TOMIX_SCRIPT_UNSUPPORTED", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task ObjectTheSerializerCannotFind_IsNotFound()
    {
        var result = await Get(new ScriptSession { Missing = true }, "Sales/Base", ObjectScriptFormat.Json);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_OBJECT_NOT_FOUND", Assert.Single(result.Diagnostics).Code);
    }

    private static Task<Core.Results.TomixResult<GetModelResult>> Get(
        IModelSession session, string path, ObjectScriptFormat? script, string? query = null)
        => new GetModelHandler([new MutationStubs.Provider(session)]).HandleAsync(
            new GetModelRequest(new ModelReference("stub"), path, query, Type: null, Script: script),
            CancellationToken.None);

    /// <summary>Scripts an object as <c>format:path:kind</c>, so a test sees exactly what it was asked.</summary>
    private sealed class ScriptSession() : MutationStubs.SnapshotSession(MutationStubs.BaseAndDerived()), IObjectScriptSession
    {
        public bool Missing { get; init; }

        public int Calls { get; private set; }

        public string ScriptObject(string path, ModelObjectKind kind, ObjectScriptFormat format)
        {
            Calls++;
            return Missing
                ? throw new ObjectNotFoundException($"Object not found: {path}")
                : $"{format}:{path}:{kind}";
        }
    }
}
