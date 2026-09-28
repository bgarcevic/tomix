using System.CommandLine;
using Spectre.Console;
using Tomix.App.Connect;
using Tomix.App.State;
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
    public static void AnnounceIfImplicit(
        ParseResult parseResult,
        string? explicitModel,
        Func<ModelReference> resolve,
        Func<CliConnectionState?>? loadSession = null)
    {
        if (string.IsNullOrWhiteSpace(explicitModel)
            && string.IsNullOrWhiteSpace(parseResult.GetValue(GlobalOptions.Server)))
            Announce(parseResult, resolve(), loadSession?.Invoke());
    }

    /// <summary>
    /// Prints the banner for a resolved reference. Callers decide whether the target was
    /// implicit; this method owns the suppression rules and rendering. Blank references
    /// (no active session) print nothing.
    /// </summary>
    /// <param name="session">
    /// The saved connection the reference came from, when the caller has it. For a Power BI
    /// Desktop session it lets the banner name the report instead of a bare port and GUID, and
    /// say when that Desktop window has been closed.
    /// </param>
    public static void Announce(ParseResult parseResult, ModelReference reference, CliConnectionState? session = null)
    {
        if (parseResult.GetValue(GlobalOptions.Quiet))
            return;

        var format = GlobalOptions.OutputFormatValue(parseResult);
        if (OutputFormats.IsJson(format) || OutputFormats.IsCsv(format))
            return;

        if (string.IsNullOrWhiteSpace(reference.Value))
            return;

        StdErr.MarkupLine(Render(reference, session, PowerBiDesktopDiscovery.IsListening, PowerBiDesktopDiscovery.StillServes));
    }

    /// <summary>The styled banner line; probes injectable so tests need no listener.</summary>
    internal static string Render(
        ModelReference reference,
        CliConnectionState? session,
        Func<string?, bool> isListening,
        Func<string?, string?, bool> stillServes)
    {
        var label = reference.IsRemote && !string.IsNullOrWhiteSpace(reference.Database)
            ? $"{reference.Value} / {reference.Database}"
            : reference.Value;

        // Only a Desktop endpoint that is the session's own server gets the Desktop treatment.
        var desktop = session is { Model: null }
            && ModelReference.IsLocalInstanceEndpoint(reference.Value)
            && string.Equals(session.Server, reference.Value, StringComparison.OrdinalIgnoreCase);
        if (!desktop)
            return Styling.Muted($"Connected to: {label}");

        if (!isListening(reference.Value))
            return Styling.Warning(
                $"Connected to: {session!.ReportName ?? label} (not running) — Power BI Desktop has closed this report; run `tx connect --local`.");

        return session!.ReportName is { } report && stillServes(session.ReportPortFile, session.Server)
            ? Styling.Muted($"Connected to: {report}  ({reference.Value})")
            : Styling.Muted($"Connected to: {label}");
    }
}
