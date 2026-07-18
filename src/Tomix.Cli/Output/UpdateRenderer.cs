using Spectre.Console;
using Tomix.Core.Update;

namespace Tomix.Cli.Output;

internal static class UpdateRenderer
{
    public static void RenderCheck(UpdateCheckResult result)
    {
        AnsiConsole.MarkupLine(Styling.Title("tx update"));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(Styling.KeyValue("Installed: ", result.CurrentVersion));
        AnsiConsole.MarkupLine(Styling.KeyValue("Latest:    ", result.LatestVersion ?? "unknown"));

        if (!result.UpdateAvailable)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(Styling.Success("tx is up to date."));
            return;
        }

        foreach (var release in result.Releases)
        {
            AnsiConsole.WriteLine();
            var header = $"v{release.Version}";
            if (release.PublishedAt is { } published)
                header += $" ({published:yyyy-MM-dd})";

            AnsiConsole.MarkupLine(release.Breaking
                ? $"{Styling.Title(Styling.MarkupEscape(header))} {Styling.Warning("[[breaking]]")}"
                : Styling.Title(Styling.MarkupEscape(header)));

            if (!string.IsNullOrWhiteSpace(release.Notes))
            {
                foreach (var line in release.Notes.ReplaceLineEndings("\n").Split('\n'))
                    AnsiConsole.MarkupLine($"  {Styling.MarkupEscape(line)}");
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(Styling.Guidance("Run 'tx update' to install."));
    }

    public static void RenderApply(UpdateApplyResult result)
    {
        AnsiConsole.MarkupLine(Styling.Success(
            $"Updated tx {result.PreviousVersion} -> {result.NewVersion} ({result.Method})."));
    }
}
