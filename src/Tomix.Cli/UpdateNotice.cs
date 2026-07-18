using System.CommandLine;
using Spectre.Console;
using Tomix.App.Update;
using Tomix.Cli.Commands;
using Tomix.Cli.Output;
using Tomix.Core.Configuration;
using Tomix.Core.Update;

namespace Tomix.Cli;

/// <summary>
/// The end-of-command "a new version is available" notice. The notice itself is served
/// purely from the on-disk cache so it never waits on the network; when the cache is
/// stale (24h TTL) the latest version is fetched inline after the command has finished,
/// bounded to 2 seconds, with every failure swallowed — an update check must never
/// change a command's output or exit code.
/// </summary>
internal static class UpdateNotice
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(2);

    public static void Run(
        ParseResult parseResult,
        string version,
        IDictionary<string, string> config,
        UpdateCheckStore store,
        IReleaseSource source)
    {
        try
        {
            var configOptOut = config.TryGetValue(ConfigKeys.UpdateCheck, out var value)
                && bool.TryParse(value, out var enabled)
                && !enabled;

            if (!ShouldShow(
                ResolveOutputFormat(parseResult),
                quiet: parseResult.GetValue(GlobalOptions.Quiet),
                stderrRedirected: Console.IsErrorRedirected,
                ciEnv: Environment.GetEnvironmentVariable("CI") is not null,
                envOptOut: Environment.GetEnvironmentVariable("TOMIX_NO_UPDATE_CHECK") is not null,
                configOptOut: configOptOut,
                kind: InstallationInspector.Detect(),
                version: version))
            {
                return;
            }

            var state = store.Load();
            if (state is not null
                && CliVersion.TryParse(state.LatestVersion, out var latest)
                && CliVersion.TryParse(version, out var current)
                && latest.IsNewerThan(current))
            {
                var errConsole = AnsiConsole.Create(new AnsiConsoleSettings
                {
                    Out = new AnsiConsoleOutput(Console.Error)
                });
                errConsole.MarkupLine(Styling.Muted(Styling.MarkupEscape(
                    $"A new version of tx is available: {version} -> {latest}. Run 'tx update' to upgrade.")));
            }

            if (store.IsStale(CheckInterval))
            {
                using var cts = new CancellationTokenSource(RefreshTimeout);
                var latestRelease = source.GetLatestAsync(cts.Token).GetAwaiter().GetResult();
                if (latestRelease is not null)
                    store.Save(latestRelease.Version);
            }
        }
        catch
        {
            // Best-effort by contract: never let the update check surface as a failure.
        }
    }

    /// <summary>
    /// Most commands define their own local <c>--output-format</c> (via
    /// <see cref="OutputFormats.CreateOption"/>) which shadows the recursive global option,
    /// so reading only <see cref="GlobalOptions.OutputFormat"/> would report <c>text</c> for
    /// e.g. <c>tx doctor --output-format json</c>. Prefer the invoked command's own option.
    /// </summary>
    internal static string ResolveOutputFormat(ParseResult parseResult)
    {
        var localOption = parseResult.CommandResult.Command.Options
            .FirstOrDefault(option => option.Name == "--output-format");
        if (localOption is Option<string> local)
            return parseResult.GetValue(local) ?? OutputFormats.Text;

        return GlobalOptions.OutputFormatValue(parseResult);
    }

    internal static bool ShouldShow(
        string outputFormat,
        bool quiet,
        bool stderrRedirected,
        bool ciEnv,
        bool envOptOut,
        bool configOptOut,
        InstallKind kind,
        string version)
    {
        if (!OutputFormats.IsTextLike(outputFormat))
            return false;
        if (quiet || stderrRedirected || ciEnv || envOptOut || configOptOut)
            return false;
        if (kind is InstallKind.Development or InstallKind.Unknown)
            return false;
        if (version == "0.0.0")
            return false;

        return true;
    }
}
