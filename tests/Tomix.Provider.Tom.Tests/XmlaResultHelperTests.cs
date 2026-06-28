using System.Reflection;
using Microsoft.AnalysisServices;

namespace Tomix.Provider.Tom.Tests;

public sealed class XmlaResultHelperTests
{
    [Fact]
    public void ExtractMessages_NoErrors_LeavesErrorsEmpty()
    {
        var results = NewCollection(NewWarning("minor"));

        var errors = new List<string>();
        var warnings = new List<string>();

        XmlaResultHelper.ExtractMessages(results, errors, warnings);

        Assert.Empty(errors);
        Assert.Single(warnings);
        Assert.Equal("minor", warnings[0]);
    }

    [Fact]
    public void ExtractMessages_XmlaError_AppendsDescription()
    {
        var results = NewCollection(
            NewError("Column 'X' not found"),
            NewError("Compatibility level mismatch"));

        var errors = new List<string>();
        var warnings = new List<string>();

        XmlaResultHelper.ExtractMessages(results, errors, warnings);

        Assert.Equal(2, errors.Count);
        Assert.Equal("Column 'X' not found", errors[0]);
        Assert.Equal("Compatibility level mismatch", errors[1]);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ExtractMessages_MixedErrorsAndWarnings_SortsIntoSeparateLists()
    {
        var results = NewCollection(
            NewWarning("w1"),
            NewError("e1"),
            NewWarning("w2"));

        var errors = new List<string>();
        var warnings = new List<string>();

        XmlaResultHelper.ExtractMessages(results, errors, warnings);

        Assert.Single(errors);
        Assert.Equal("e1", errors[0]);
        Assert.Equal(2, warnings.Count);
        Assert.Equal("w1", warnings[0]);
        Assert.Equal("w2", warnings[1]);
    }

    [Fact]
    public void ExtractMessages_WarningsNull_SkipsWarningCollection()
    {
        var results = NewCollection(
            NewWarning("ignored"),
            NewError("kept"));

        var errors = new List<string>();

        XmlaResultHelper.ExtractMessages(results, errors, warnings: null);

        Assert.Single(errors);
        Assert.Equal("kept", errors[0]);
    }

    [Fact]
    public void ExtractMessages_NullOrEmpty_IsNoOp()
    {
        var errors = new List<string>();

        XmlaResultHelper.ExtractMessages(null, errors);
        Assert.Empty(errors);

        XmlaResultHelper.ExtractMessages(NewCollection(), errors);
        Assert.Empty(errors);
    }

    [Fact]
    public void ExtractMessages_AcrossMultipleResults_AggregatesAll()
    {
        var collection = (XmlaResultCollection)Activator.CreateInstance(typeof(XmlaResultCollection), nonPublic: true)!;
        AddResult(collection, NewError("first"));
        AddResult(collection, NewError("second"));

        var errors = new List<string>();

        XmlaResultHelper.ExtractMessages(collection, errors);

        Assert.Equal(2, errors.Count);
        Assert.Equal("first", errors[0]);
        Assert.Equal("second", errors[1]);
    }

    // The AMO XMLA result types expose only non-public constructors (they are server-created), so
    // the fixtures below use reflection to build them. This is the only way to unit-test extraction
    // without a live Analysis Services connection.
    private static XmlaResultCollection NewCollection(params XmlaMessage[] messages)
    {
        var collection = (XmlaResultCollection)Activator.CreateInstance(typeof(XmlaResultCollection), nonPublic: true)!;
        if (messages.Length > 0)
            AddResult(collection, messages);
        return collection;
    }

    private static void AddResult(XmlaResultCollection collection, params XmlaMessage[] messages)
    {
        var result = (XmlaResult)Activator.CreateInstance(typeof(XmlaResult), nonPublic: true)!;
        var addMessage = typeof(XmlaMessageCollection).GetMethod(
            "Add", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var message in messages)
            addMessage!.Invoke(result.Messages, [message]);

        typeof(XmlaResultCollection).GetMethod(
            "Add", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(collection, [result]);
    }

    private static XmlaError NewError(string description)
        => (XmlaError)CreateMessage(typeof(XmlaError), description);

    private static XmlaWarning NewWarning(string description)
        => (XmlaWarning)CreateMessage(typeof(XmlaWarning), description);

    private static XmlaMessage CreateMessage(Type type, string description)
        => (XmlaMessage)Activator.CreateInstance(
            type,
            bindingAttr: BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            args: [0, description, null, null, null],
            culture: null)!;
}
