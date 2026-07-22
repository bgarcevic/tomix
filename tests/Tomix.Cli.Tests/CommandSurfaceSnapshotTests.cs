using System.CommandLine;
using System.CommandLine.Help;
using System.Runtime.CompilerServices;
using System.Text;
using Tomix.App.Format;

namespace Tomix.Cli.Tests;

/// <summary>
/// Guards the docs site against silent drift: the reference pages under docs/commands/
/// document every command's arguments and options, so any change to the command surface
/// must be reflected there. This test snapshots the surface (command paths, descriptions,
/// arguments, options) from the parser object model — not from rendered help, so it is
/// independent of console width and styling. When it fails: update the affected pages
/// under docs/commands/, then regenerate the snapshot with
/// <c>TOMIX_UPDATE_SNAPSHOTS=1 dotnet test --filter CommandSurfaceSnapshotTests</c>.
/// </summary>
public sealed class CommandSurfaceSnapshotTests
{
    [Fact]
    public void CommandSurface_MatchesApprovedSnapshot()
    {
        var actual = RenderSurface();
        var path = SnapshotPath();

        if (Environment.GetEnvironmentVariable("TOMIX_UPDATE_SNAPSHOTS") == "1")
        {
            File.WriteAllText(path, actual);
            return;
        }

        var approved = File.Exists(path)
            ? Normalize(File.ReadAllText(path))
            : "";

        if (approved == Normalize(actual))
            return;

        Assert.Fail(
            "The CLI command surface changed but the approved snapshot did not." + Environment.NewLine +
            "1. Update the affected reference pages under docs/commands/." + Environment.NewLine +
            "2. Regenerate: TOMIX_UPDATE_SNAPSHOTS=1 dotnet test --filter CommandSurfaceSnapshotTests" + Environment.NewLine +
            Environment.NewLine +
            DescribeFirstDifferences(approved, Normalize(actual)));
    }

    /// <summary>
    /// The snapshot alone can be regenerated without touching the docs, so this second
    /// gate makes new commands impossible to ship undocumented: every command path must
    /// appear verbatim somewhere in docs/commands/*.md. It cannot prove the surrounding
    /// prose is accurate — option-level accuracy stays on the snapshot-diff review.
    /// </summary>
    [Fact]
    public void EveryCommand_IsMentionedInCommandDocs()
    {
        var docsDir = Path.Combine(RepoRoot(), "docs", "commands");
        var haystack = string.Join('\n', Directory.GetFiles(docsDir, "*.md").Select(File.ReadAllText));

        var missing = CommandPaths(BuildRoot(), "")
            .Select(entry => entry.Path.TrimStart(' '))
            .Where(path => !haystack.Contains(path, StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0,
            "Commands not mentioned anywhere in docs/commands/*.md — document them on the appropriate page:" +
            Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    private static RootCommand BuildRoot()
        => Program.BuildRootCommand(
            providers: [],
            new CompositeExpressionFormatterClient([]),
            version: "0.0.0-test",
            TestServices.Create());

    private static string RenderSurface()
    {
        var root = BuildRoot();

        var sb = new StringBuilder();
        WriteCommand(sb, root, "tx");
        foreach (var (command, path) in CommandPaths(root, "tx"))
            WriteCommand(sb, command, path);
        return sb.ToString();
    }

    private static void WriteCommand(StringBuilder sb, Command command, string path)
    {
        sb.Append("# ").Append(path);
        if (command.Aliases.Count > 0)
            sb.Append(" (aliases: ").Append(string.Join(", ", command.Aliases.OrderBy(a => a, StringComparer.Ordinal))).Append(')');
        sb.Append('\n');
        sb.Append(OneLine(command.Description)).Append('\n');

        foreach (var arg in command.Arguments.Where(a => !a.Hidden))
        {
            var label = arg.Arity.MinimumNumberOfValues == 0 ? $"[{arg.Name}]" : $"<{arg.Name}>";
            sb.Append("  arg ").Append(label).Append("  ").Append(OneLine(arg.Description)).Append('\n');
        }

        foreach (var opt in command.Options.Where(o => !o.Hidden))
            sb.Append("  opt ").Append(FormatOption(opt)).Append("  ").Append(OneLine(opt.Description)).Append('\n');

        sb.Append('\n');
    }

    // Mirrors the alias ordering and value placeholder of SpectreHelpAction so the
    // snapshot reads like the help output it stands in for.
    private static string FormatOption(Option option)
    {
        var names = new List<string> { option.Name };
        names.AddRange(option.Aliases);
        var joined = string.Join(", ", names
            .OrderByDescending(n => n.StartsWith("--"))
            .ThenBy(n => n.Length)
            .ThenBy(n => n));

        if (option is { ValueType: not null } && option.ValueType != typeof(bool) && option.ValueType != typeof(bool?)
            && option is not HelpOption and not VersionOption)
        {
            joined += $" <{option.Name.TrimStart('-')}>";
        }

        return joined;
    }

    private static IEnumerable<(Command Command, string Path)> CommandPaths(Command command, string prefix)
    {
        foreach (var sub in command.Subcommands.Where(c => !c.Hidden))
        {
            var path = $"{prefix} {sub.Name}";
            yield return (sub, path);
            foreach (var nested in CommandPaths(sub, path))
                yield return nested;
        }
    }

    private static string OneLine(string? text)
        => (text ?? "").Replace("\r", "").Replace("\n", "\\n");

    private static string Normalize(string text)
        => text.Replace("\r\n", "\n");

    private static string DescribeFirstDifferences(string approved, string actual)
    {
        var approvedLines = approved.Split('\n');
        var actualLines = actual.Split('\n');
        var diffs = new List<string>();

        for (var i = 0; i < Math.Max(approvedLines.Length, actualLines.Length) && diffs.Count < 10; i++)
        {
            var left = i < approvedLines.Length ? approvedLines[i] : "<missing>";
            var right = i < actualLines.Length ? actualLines[i] : "<missing>";
            if (left != right)
                diffs.Add($"line {i + 1}:{Environment.NewLine}  approved: {left}{Environment.NewLine}  actual:   {right}");
        }

        return string.Join(Environment.NewLine, diffs);
    }

    private static string SnapshotPath([CallerFilePath] string sourcePath = "")
        => Path.Combine(Path.GetDirectoryName(sourcePath)!, "CommandSurface.approved.txt");

    private static string RepoRoot([CallerFilePath] string sourcePath = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", ".."));
}
