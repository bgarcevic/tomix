using Spectre.Console;
using Spectre.Console.Testing;
using Tomix.App.Connect;
using Tomix.Cli.Commands;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

public class ConnectPromptsTests
{
    private static TestConsole Interactive() => new TestConsole().Interactive();

    private static ModelReference Endpoint => ModelReference.Remote("powerbi://api.powerbi.com/v1.0/myorg/WS");

    // Selecting the highlighted (first) workspace returns it.
    [Fact]
    public async Task PickWorkspace_SelectsHighlighted()
    {
        var console = Interactive();
        console.Input.PushKey(ConsoleKey.Enter);
        var catalog = new FakeWorkspaceCatalog(
            new WorkspaceInfo("1", "Alpha", IsOnDedicatedCapacity: true),
            new WorkspaceInfo("2", "Beta", IsOnDedicatedCapacity: true));

        var result = await ConnectPrompts.PickWorkspaceAsync(console, catalog, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Alpha", result!.Name);
    }

    // Workspaces without XMLA capacity are hidden from the picker, with a note explaining why.
    [Fact]
    public async Task PickWorkspace_HidesNonPremium_WithNote()
    {
        var console = Interactive();
        console.Input.PushKey(ConsoleKey.Enter);
        var catalog = new FakeWorkspaceCatalog(
            new WorkspaceInfo("1", "Premium", IsOnDedicatedCapacity: true),
            new WorkspaceInfo("2", "Shared", IsOnDedicatedCapacity: false));

        var result = await ConnectPrompts.PickWorkspaceAsync(console, catalog, CancellationToken.None);

        Assert.Equal("Premium", result!.Name);
        Assert.Contains("Hidden 1 workspace", console.Output);
    }

    // No XMLA-capable workspaces: return null and explain, rather than showing an empty picker.
    [Fact]
    public async Task PickWorkspace_NoneCapable_ReturnsNull()
    {
        var console = Interactive();
        var catalog = new FakeWorkspaceCatalog(new WorkspaceInfo("2", "Shared", IsOnDedicatedCapacity: false));

        var result = await ConnectPrompts.PickWorkspaceAsync(console, catalog, CancellationToken.None);

        Assert.Null(result);
    }

    // Selecting an existing model returns its name (not a workspace-only choice).
    [Fact]
    public async Task PickDatabase_ExistingSelected_ReturnsName()
    {
        var console = Interactive();
        console.Input.PushKey(ConsoleKey.Enter);
        var catalog = new FakeServerCatalog("SalesModel");

        var result = await ConnectPrompts.PickDatabaseAsync(
            console, catalog, Endpoint,
            allowCreateNew: false, allowWorkspaceOnly: false, suggestedNewName: null, CancellationToken.None);

        Assert.False(result.IsWorkspaceOnly);
        Assert.Equal("SalesModel", result.Name);
    }

    // "Create new" with only the create entry offered: selecting it and pressing Enter accepts
    // the pre-filled suggestion.
    [Fact]
    public async Task PickDatabase_CreateNew_AcceptsSuggestion()
    {
        var console = Interactive();
        console.Input.PushKey(ConsoleKey.Enter); // select the (only) "create new" entry
        console.Input.PushKey(ConsoleKey.Enter); // accept the default suggestion
        var catalog = new FakeServerCatalog();

        var result = await ConnectPrompts.PickDatabaseAsync(
            console, catalog, Endpoint,
            allowCreateNew: true, allowWorkspaceOnly: false, suggestedNewName: "sales-dev-bokg", CancellationToken.None);

        Assert.False(result.IsWorkspaceOnly);
        Assert.Equal("sales-dev-bokg", result.Name);
    }

    // "Create new" with a typed name overrides the suggestion.
    [Fact]
    public async Task PickDatabase_CreateNew_TypedName()
    {
        var console = Interactive();
        console.Input.PushKey(ConsoleKey.Enter);       // select "create new"
        console.Input.PushTextWithEnter("custom-name"); // type a name
        var catalog = new FakeServerCatalog();

        var result = await ConnectPrompts.PickDatabaseAsync(
            console, catalog, Endpoint,
            allowCreateNew: true, allowWorkspaceOnly: false, suggestedNewName: "sales-dev-bokg", CancellationToken.None);

        Assert.Equal("custom-name", result.Name);
    }

    // "Workspace only" is an explicit choice, distinct from a failure.
    [Fact]
    public async Task PickDatabase_WorkspaceOnly_ReturnsWorkspaceOnly()
    {
        var console = Interactive();
        console.Input.PushKey(ConsoleKey.Enter);
        var catalog = new FakeServerCatalog();

        var result = await ConnectPrompts.PickDatabaseAsync(
            console, catalog, Endpoint,
            allowCreateNew: false, allowWorkspaceOnly: true, suggestedNewName: null, CancellationToken.None);

        Assert.True(result.IsWorkspaceOnly);
        Assert.Null(result.Name);
    }

    // A listing failure (e.g. expired auth) surfaces as an exception — it must never be mistaken
    // for an explicit "workspace only" choice by callers.
    [Fact]
    public async Task PickDatabase_ListingFails_Throws()
    {
        var console = Interactive();
        var catalog = new FakeServerCatalog { Failure = new InvalidOperationException("XMLA denied") };

        await Assert.ThrowsAsync<InvalidOperationException>(() => ConnectPrompts.PickDatabaseAsync(
            console, catalog, Endpoint,
            allowCreateNew: false, allowWorkspaceOnly: true, suggestedNewName: null, CancellationToken.None));
    }
}
