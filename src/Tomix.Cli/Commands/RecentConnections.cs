using System.CommandLine;
using System.Globalization;
using Spectre.Console;
using Tomix.App.State;
using Tomix.Cli.Output;

namespace Tomix.Cli.Commands;

/// <summary>
/// Shared resolution for the global --recent option: picks an entry from the
/// recent-connections MRU, either by 1-based index (--recent N) or via an
/// interactive picker on stderr (bare --recent on a TTY).
/// </summary>
internal static class RecentConnections
{
    /// <summary>
    /// The model/server/database triple a command should resolve its model from,
    /// after the --recent override (if any) has been applied. <see cref="RecentEntry"/>
    /// holds the picked connection when --recent was used (else null); callers must resolve
    /// against it — not the active session — so a recent never inherits the active
    /// connection's database or workspace mirror. Build the resolver via
    /// <see cref="CreateResolver"/>.
    /// </summary>
    public readonly record struct ModelSource(
        string? Model,
        string? Server,
        string? Database,
        CliConnectionState? RecentEntry);

    /// <summary>
    /// Reads the --server/--database globals and applies the --recent override on top:
    /// when --recent is passed, the picked entry supplies model/server (an explicit
    /// --database still wins over the entry's). Does not touch the active connection.
    /// Returns false with <paramref name="exitCode"/> set when --recent conflicts with
    /// an explicit model/--server or cannot be resolved.
    /// </summary>
    public static bool TryGetSource(
        ParseResult parseResult,
        string? explicitModel,
        CliStateStore store,
        out ModelSource source,
        out int exitCode)
    {
        var server = parseResult.GetValue(GlobalOptions.Server);
        var database = parseResult.GetValue(GlobalOptions.Database);
        exitCode = 0;

        if (!GlobalOptions.RecentSpecified(parseResult))
        {
            source = new ModelSource(explicitModel, server, database, RecentEntry: null);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(explicitModel) || !string.IsNullOrWhiteSpace(server))
        {
            WriteError("--recent cannot be combined with a model path or --server.");
            source = default;
            exitCode = 2;
            return false;
        }

        if (!TryResolve(parseResult, store, out var entry, out exitCode))
        {
            source = default;
            return false;
        }

        source = new ModelSource(
            entry!.Connection.Model,
            entry.Connection.Server,
            string.IsNullOrWhiteSpace(database) ? entry.Connection.Database : database,
            entry.Connection);
        return true;
    }

    /// <summary>
    /// Builds the resolver a command should use for <paramref name="source"/>. For a --recent
    /// selection the resolver is seeded with the picked entry so both <c>ResolveReference</c>
    /// and <c>ResolveSyncTarget</c> draw the database and workspace mirror from that entry,
    /// never the active session. Otherwise it reads the active session as usual.
    /// </summary>
    public static ActiveModelResolver CreateResolver(ModelSource source, CliStateStore store)
        => source.RecentEntry is null
            ? new ActiveModelResolver(store)
            : new ActiveModelResolver(() => source.RecentEntry);

    /// <summary>
    /// The standard model-resolution prologue in one call: applies the --recent override via
    /// <see cref="TryGetSource"/>, then resolves the triple to a <see cref="Tomix.Core.Models.ModelReference"/>.
    /// Use the two-step form only when the command also needs the intermediate
    /// <see cref="ModelSource"/> (e.g. for sync targets).
    /// </summary>
    public static bool TryResolveModel(
        ParseResult parseResult,
        string? explicitModel,
        CliStateStore store,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Tomix.Core.Models.ModelReference? reference,
        out int exitCode)
    {
        if (!TryGetSource(parseResult, explicitModel, store, out var source, out exitCode))
        {
            reference = null;
            return false;
        }

        reference = CreateResolver(source, store).ResolveReference(source.Model, source.Database, source.Server);
        return true;
    }

    /// <summary>
    /// The session source a handler that resolves in the App layer (refresh, deploy) should use
    /// for <paramref name="source"/>: the picked --recent entry, or null to read the active session.
    /// </summary>
    public static Func<CliConnectionState?>? SessionSource(ModelSource source)
        => source.RecentEntry is null ? null : () => source.RecentEntry;

    /// <summary>
    /// Resolves --recent to a stored recent connection. Returns false with
    /// <paramref name="exitCode"/> set on invalid input, empty MRU, out-of-range
    /// index, non-interactive bare --recent, or picker cancel (exit 0).
    /// </summary>
    public static bool TryResolve(
        ParseResult parseResult,
        CliStateStore store,
        out RecentConnection? entry,
        out int exitCode)
    {
        entry = null;
        exitCode = 0;

        var raw = GlobalOptions.RecentValue(parseResult);
        if (!TryParseRecentIndex(raw, out var index))
        {
            WriteError($"Invalid --recent value '{raw}'. Expected a number: 1 = most recent.");
            exitCode = 2;
            return false;
        }

        var recents = store.LoadRecentConnections();
        if (recents.Count == 0)
        {
            WriteError("No recent connections yet.");
            WriteGuidance("Connect once: tx connect <server> <database>");
            exitCode = 1;
            return false;
        }

        if (index >= 1)
        {
            if (index > recents.Count)
            {
                var noun = recents.Count == 1 ? "connection" : "connections";
                WriteError($"--recent {index} is out of range ({recents.Count} recent {noun}). Run: tx connect --recent");
                exitCode = 1;
                return false;
            }

            entry = recents[index - 1];
            return true;
        }

        if (!InteractionGate.CanPrompt(parseResult, GlobalOptions.OutputFormatValue(parseResult)))
        {
            WriteError("--recent needs an index when prompts are unavailable.");
            WriteGuidance("Use --recent <n>; list with: tx connect --recent");
            exitCode = 2;
            return false;
        }

        var selected = Prompt(recents);
        if (selected is null)
        {
            StdErr().MarkupLine(Styling.Muted("Cancelled."));
            exitCode = 0;
            return false;
        }

        entry = selected;
        return true;
    }

    private static RecentConnection? Prompt(IReadOnlyList<RecentConnection> recents)
    {
        var now = DateTimeOffset.UtcNow;
        var prompt = new SelectionPrompt<int>()
            .Title(Styling.Bold("Recent connections"))
            .PageSize(10)
            .WrapAround()
            .UseConverter(choice => choice < 0
                ? Styling.Muted("(cancel)")
                : $"{Styling.MarkupEscape(FormatRecentLabel(recents[choice].Connection))}  {Styling.Muted(FormatRecentAge(recents[choice].LastUsed, now))}");

        prompt.AddChoices(Enumerable.Range(0, recents.Count));
        prompt.AddChoice(-1);

        var picked = StdErr().Prompt(prompt);
        return picked < 0 ? null : recents[picked];
    }

    /// <summary>
    /// Parses the optional --recent value: no value means "prompt" (index 0);
    /// otherwise a 1-based index. Zero, negative, and non-numeric values fail.
    /// </summary>
    internal static bool TryParseRecentIndex(string? raw, out int index)
    {
        if (raw is null)
        {
            index = 0;
            return true;
        }

        if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index >= 1)
            return true;

        index = 0;
        return false;
    }

    internal static string FormatRecentLabel(CliConnectionState connection)
    {
        var label = !string.IsNullOrWhiteSpace(connection.Model)
            ? connection.Model
            : string.IsNullOrWhiteSpace(connection.Database)
                ? connection.Server ?? ""
                : $"{connection.Server} / {connection.Database}";

        if (!string.IsNullOrWhiteSpace(connection.Profile))
            label += $" (profile: {connection.Profile})";
        if (!string.IsNullOrWhiteSpace(connection.Workspace))
            label += $" (mirror: {connection.Workspace})";

        return label;
    }

    internal static string FormatRecentAge(DateTimeOffset lastUsed, DateTimeOffset now)
    {
        var age = now - lastUsed;
        if (age < TimeSpan.FromMinutes(1))
            return "just now";
        if (age < TimeSpan.FromHours(1))
            return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1))
            return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

    private static void WriteError(string message)
        => StdErr().MarkupLine(Styling.Error(message));

    private static void WriteGuidance(string message)
        => StdErr().MarkupLine(Styling.Guidance(message));

    private static IAnsiConsole StdErr()
        => AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
}
