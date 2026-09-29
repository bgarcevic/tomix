using System.CommandLine;
using System.Runtime.CompilerServices;

namespace Tomix.Cli.Output;

/// <summary>
/// Assigns options to a named section of their command's help ("Save options:", ...). Untagged
/// options land under "Options:". Commands with many flags tag them so the help reads in blocks
/// instead of one long list; the shared lifecycle flags are tagged by their factory.
/// </summary>
internal static class HelpGroups
{
    public const string Save = "Save options";

    private static readonly ConditionalWeakTable<Option, string> Groups = new();

    /// <summary>Tags <paramref name="option"/> with <paramref name="group"/> and returns it.</summary>
    public static T In<T>(this T option, string group) where T : Option
    {
        Groups.AddOrUpdate(option, group);
        return option;
    }

    public static string? Of(Option option)
        => Groups.TryGetValue(option, out var group) ? group : null;
}
