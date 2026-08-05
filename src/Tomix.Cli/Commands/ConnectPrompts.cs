using System.Text;
using Spectre.Console;
using Tomix.App.Connect;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

/// <summary>
/// Interactive pickers for the <c>connect</c> command: choose a workspace from the Power BI
/// tenant, then a semantic model within it (optionally creating a new mirror name). Every
/// method renders on the supplied <see cref="IAnsiConsole"/> (stderr in production, a
/// <c>TestConsole</c> in tests) so nothing pollutes stdout, and each is only reached when
/// <see cref="InteractionGate.CanPrompt(System.CommandLine.ParseResult, string)"/> is true.
/// </summary>
internal static class ConnectPrompts
{
    /// <summary>
    /// Outcome of the model picker: either a chosen/created model name, or an explicit
    /// "connect to workspace only" choice. A listing failure is signalled by an exception,
    /// never by this type, so callers can tell a real selection from a failure.
    /// </summary>
    internal readonly record struct DatabaseSelection(bool IsWorkspaceOnly, string? Name)
    {
        public static readonly DatabaseSelection WorkspaceOnly = new(true, null);

        public static DatabaseSelection ForModel(string name) => new(false, name);
    }

    /// <summary>Prompts for a workspace, returning null when none can be listed.</summary>
    public static async Task<WorkspaceInfo?> PickWorkspaceAsync(
        IAnsiConsole console,
        IWorkspaceCatalog catalog,
        CancellationToken cancellationToken)
    {
        var workspaces = await console.Status()
            .Spinner(Spectre.Console.Spinner.Known.Dots)
            .StartAsync(
                "Listing workspaces (api.powerbi.com)...",
                _ => catalog.ListWorkspacesAsync(cancellationToken))
            .ConfigureAwait(false);

        var xmlaCapable = workspaces.Where(w => w.IsOnDedicatedCapacity).ToList();
        var hidden = workspaces.Count - xmlaCapable.Count;
        if (hidden > 0)
            console.MarkupLine(Styling.Muted(
                $"Hidden {hidden} workspace(s) without XMLA capacity (Premium/PPU only)."));

        if (xmlaCapable.Count == 0)
        {
            console.MarkupLine(Styling.Error(
                "No XMLA-capable workspaces found for this account. Ask an admin for access to a "
                + "Premium or PPU workspace, or pass an endpoint directly: tx connect powerbi://... <model>."));
            return null;
        }

        var prompt = new SelectionPrompt<WorkspaceInfo>()
            .Title("Select a [green]workspace[/]:")
            .PageSize(15)
            .EnableSearch()
            .UseConverter(w => Styling.MarkupEscape(w.Name));
        prompt.AddChoices(xmlaCapable);

        return await console.PromptAsync(prompt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A running Desktop instance as one line: the report name when it could be read, with the
    /// endpoint always shown so it can be passed to <c>--server</c>.
    /// </summary>
    public static string DescribeInstance(PowerBiDesktopInstance instance)
        => instance.ReportName is null
            ? instance.Endpoint
            : $"{instance.ReportName}  ({instance.Endpoint})";

    /// <summary>
    /// Prompts for one of several running Power BI Desktop instances. Unlike the model picker
    /// there is no listing call and nothing can fail: the instances are already known, so this
    /// always returns one of them.
    /// </summary>
    public static async Task<PowerBiDesktopInstance> PickDesktopInstanceAsync(
        IAnsiConsole console,
        IReadOnlyList<PowerBiDesktopInstance> instances,
        CancellationToken cancellationToken)
    {
        var prompt = new SelectionPrompt<PowerBiDesktopInstance>()
            .Title("Select a [green]Power BI Desktop[/] instance:")
            .PageSize(15)
            .UseConverter(instance => Styling.MarkupEscape(DescribeInstance(instance)));
        prompt.AddChoices(instances);

        return await console.PromptAsync(prompt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The models a lister produced, plus an optional note to show once the spinner is gone
    /// (for example: the fast REST listing failed and the slower XMLA one was used instead).
    /// </summary>
    internal readonly record struct ModelListing(IReadOnlyList<ServerDatabaseInfo> Models, string? Note);

    /// <summary>
    /// Prompts for a semantic model produced by <paramref name="list"/> — the caller decides
    /// whether that is the REST dataset API or an XMLA catalog, so this prompt stays protocol
    /// agnostic. When <paramref name="allowCreateNew"/> is set, offers a "create new" entry that
    /// opens a text prompt pre-filled with <paramref name="suggestedNewName"/>. When
    /// <paramref name="allowWorkspaceOnly"/> is set, offers a "workspace only" entry.
    /// Returns a <see cref="DatabaseSelection"/> distinguishing a chosen/created model from an
    /// explicit "workspace only" choice. Throws (rather than returning a sentinel) if listing
    /// fails or nothing can be offered, so callers never mistake a failure for "workspace only".
    /// </summary>
    public static async Task<DatabaseSelection> PickDatabaseAsync(
        IAnsiConsole console,
        Func<CancellationToken, Task<ModelListing>> list,
        bool allowCreateNew,
        bool allowWorkspaceOnly,
        string? suggestedNewName,
        CancellationToken cancellationToken)
    {
        var listing = await console.Status()
            .Spinner(Spectre.Console.Spinner.Known.Dots)
            .StartAsync(
                "Listing models on the workspace...",
                _ => list(cancellationToken))
            .ConfigureAwait(false);

        // Printed after the status display has torn down; writing inside the callback would
        // fight the live region for the same lines. Styling.Muted escapes, which matters here:
        // a note can carry a server error message, and those are free to contain brackets.
        if (!string.IsNullOrWhiteSpace(listing.Note))
            console.MarkupLine(Styling.Muted(listing.Note));

        var choices = new List<DatabaseChoice>();
        foreach (var database in listing.Models.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            choices.Add(DatabaseChoice.Existing(database.Name));
        if (allowCreateNew)
            choices.Add(DatabaseChoice.CreateNew());
        if (allowWorkspaceOnly)
            choices.Add(DatabaseChoice.WorkspaceOnly());

        if (choices.Count == 0)
            throw new InvalidOperationException(
                "No semantic models found on the workspace, and creating one is not offered here. "
                + "Deploy a model first, or pass a model name explicitly.");

        var prompt = new SelectionPrompt<DatabaseChoice>()
            .Title("Select a [green]semantic model[/]:")
            .PageSize(15)
            .EnableSearch()
            .UseConverter(c => c.Label);
        prompt.AddChoices(choices);

        var choice = await console.PromptAsync(prompt, cancellationToken).ConfigureAwait(false);
        return choice.Kind switch
        {
            DatabaseChoiceKind.Existing => DatabaseSelection.ForModel(choice.Name),
            DatabaseChoiceKind.WorkspaceOnly => DatabaseSelection.WorkspaceOnly,
            _ => DatabaseSelection.ForModel(await PromptNewNameAsync(console, suggestedNewName, cancellationToken).ConfigureAwait(false))
        };
    }

    private static async Task<string> PromptNewNameAsync(
        IAnsiConsole console,
        string? suggestedNewName,
        CancellationToken cancellationToken)
    {
        var prompt = new TextPrompt<string>("New model name:")
            .Validate(name => string.IsNullOrWhiteSpace(name)
                ? ValidationResult.Error("Enter a model name.")
                : ValidationResult.Success());
        if (!string.IsNullOrWhiteSpace(suggestedNewName))
            prompt.DefaultValue(suggestedNewName);

        var name = await console.PromptAsync(prompt, cancellationToken).ConfigureAwait(false);
        return name.Trim();
    }

    /// <summary>
    /// Suggests a mirror database name of the form <c>&lt;model&gt;-dev-&lt;user&gt;</c>, so parallel
    /// devs mirroring the same model into one workspace don't collide. Both parts are slugged;
    /// the user segment is dropped when no cached username is available.
    /// </summary>
    internal static string SuggestMirrorDatabaseName(string? modelName, string? username)
    {
        var model = Slug(modelName);
        if (string.IsNullOrEmpty(model))
            model = "model";

        var user = Slug(StripDomain(username));
        return string.IsNullOrEmpty(user) ? $"{model}-dev" : $"{model}-dev-{user}";
    }

    private static string? StripDomain(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return username;

        var at = username.IndexOf('@');
        return at > 0 ? username[..at] : username;
    }

    private static string Slug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var builder = new StringBuilder(value.Length);
        var lastWasDash = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private enum DatabaseChoiceKind
    {
        Existing,
        CreateNew,
        WorkspaceOnly
    }

    private sealed record DatabaseChoice(DatabaseChoiceKind Kind, string Name, string Label)
    {
        public static DatabaseChoice Existing(string name)
            => new(DatabaseChoiceKind.Existing, name, Styling.MarkupEscape(name));

        public static DatabaseChoice CreateNew()
            => new(DatabaseChoiceKind.CreateNew, "", "[green]+ Create new model...[/]");

        public static DatabaseChoice WorkspaceOnly()
            => new(DatabaseChoiceKind.WorkspaceOnly, "", "[grey]Connect to workspace only (choose model later)[/]");
    }
}
