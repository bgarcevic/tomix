using System.CommandLine;
using Spectre.Console;
using Tomix.Cli.Output;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

/// <summary>
/// The one-line "Connected to: …" announcement commands emit when the model they are about to
/// open was resolved implicitly from the saved active connection, so the user can always see
/// which model a command operates on. Always written to stderr (piping stdout is unaffected),
/// suppressed under <c>--quiet</c> and for machine output (JSON/CSV).
/// </summary>
internal static class ConnectionBanner
{
    /// <summary>
    /// Announces the target when it resolves from the active connection: an explicit model path
    /// or <c>--server</c> means the user named the target themselves and the line is skipped.
    /// <paramref name="resolve"/> is only invoked for implicit targets. Commands whose
    /// <c>--server</c> addresses something other than the model (e.g. deploy) gate on their own
    /// explicitness and call <see cref="Announce"/> directly.
    /// </summary>
    public static void AnnounceIfImplicit(ParseResult parseResult, string? explicitModel, Func<ModelReference> resolve)
    {
        if (string.IsNullOrWhiteSpace(explicitModel)
            && string.IsNullOrWhiteSpace(parseResult.GetValue(GlobalOptions.Server)))
            Announce(parseResult, resolve());
    }

    /// <summary>
    /// Prints the banner for a resolved reference. Callers decide whether the target was
    /// implicit; this method owns the suppression rules and rendering. Blank references
    /// (no active session) print nothing.
    /// </summary>
    public static void Announce(ParseResult parseResult, ModelReference reference)
    {
        if (parseResult.GetValue(GlobalOptions.Quiet))
            return;

        var format = GlobalOptions.OutputFormatValue(parseResult);
        if (OutputFormats.IsJson(format) || OutputFormats.IsCsv(format))
            return;

        var label = reference.IsRemote && !string.IsNullOrWhiteSpace(reference.Database)
            ? $"{reference.Value} / {reference.Database}"
            : reference.Value;
        if (string.IsNullOrWhiteSpace(label))
            return;

        StdErr().MarkupLine(Styling.Muted($"Connected to: {label}"));
    }

    private static IAnsiConsole StdErr()
        => AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
}
