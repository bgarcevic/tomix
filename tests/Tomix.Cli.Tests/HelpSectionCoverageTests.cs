using System.CommandLine;
using Tomix.App.Format;
using Tomix.Cli.Output;

namespace Tomix.Cli.Tests;

/// <summary>
/// The root help groups commands via a hand-maintained section list. These tests keep that list
/// in two-way parity with the commands actually registered in <c>Program.BuildRootCommand</c>,
/// so a new command can neither vanish from <c>tx --help</c> nor linger in help after removal.
/// </summary>
public sealed class HelpSectionCoverageTests
{
    private static RootCommand BuildRoot()
        => Program.BuildRootCommand(
            providers: [],
            new CompositeExpressionFormatterClient([]),
            version: "0.0.0-test",
            TestServices.Create());

    private static HashSet<string> ListedNames()
        => SpectreHelpAction.RootSections.SelectMany(section => section.Commands)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void EveryRegisteredCommand_AppearsInAHelpSection()
    {
        var listed = ListedNames();
        var missing = BuildRoot().Subcommands
            .Select(sc => sc.Name)
            .Where(name => !listed.Contains(name))
            .ToList();

        Assert.True(missing.Count == 0,
            $"Commands registered but absent from HelpRenderer sections: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EverySectionedCommand_IsActuallyRegistered()
    {
        var registered = BuildRoot().Subcommands
            .Select(sc => sc.Name)
            .ToHashSet(StringComparer.Ordinal);
        var stale = ListedNames().Where(name => !registered.Contains(name)).ToList();

        Assert.True(stale.Count == 0,
            $"HelpRenderer sections list commands that are not registered: {string.Join(", ", stale)}");
    }

    [Fact]
    public void SectionedCommands_HaveNoDuplicates()
    {
        var all = SpectreHelpAction.RootSections.SelectMany(section => section.Commands)
            .ToList();
        var duplicates = all.GroupBy(name => name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.True(duplicates.Count == 0,
            $"Commands listed in more than one help section: {string.Join(", ", duplicates)}");
    }
}
