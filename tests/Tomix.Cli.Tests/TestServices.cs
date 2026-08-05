using System.Collections.Concurrent;
using System.CommandLine;
using Tomix.App;
using Tomix.App.Format;
using Tomix.Cli.Commands;

namespace Tomix.Cli.Tests;

/// <summary>
/// <see cref="AppServices"/> rooted in a throwaway temp directory so command tests never read
/// the developer's real <c>~/.tomix</c>. Construction creates no files; tests that only parse
/// or render leave the directory nonexistent.
/// </summary>
internal static class TestServices
{
    private static readonly ConcurrentBag<string> Roots = [];

    // The roots a test actually writes to (config show, connect, doctor) used to survive the run:
    // roughly five stray tomix-cli-tests-* directories in $TMPDIR per `dotnet test`. There is no
    // single owner to dispose — a root outlives the AppServices handed to each test — so they are
    // swept when the test host exits.
    static TestServices() =>
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var root in Roots)
            {
                try
                {
                    if (Directory.Exists(root))
                        Directory.Delete(root, recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Best-effort: the run is over, so a locked directory must not fail it.
                }
            }
        };

    public static AppServices Create()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tomix-cli-tests-{Guid.NewGuid():N}");
        Roots.Add(root);
        return AppServices.Create(root);
    }
}

/// <summary>
/// Root commands for parse and invoke tests. <see cref="With"/> is the narrow root — global options
/// plus only the subcommands under test; <see cref="Full"/> is the real production tree.
/// </summary>
/// <remarks>
/// Replaces the hand-rolled <c>new RootCommand("test")</c> + <c>GlobalOptions.All()</c> loop that
/// appeared in twenty-two places, and the three byte-identical <c>BuildRoot()</c> copies wrapping
/// <see cref="Program.BuildRootCommand"/>.
/// </remarks>
internal static class TestRoot
{
    /// <summary>The version stamped on <see cref="Full"/>, so no test depends on the real one.</summary>
    public const string Version = "0.0.0-test";

    /// <summary>A root carrying the global options and <paramref name="subcommands"/>.</summary>
    public static RootCommand With(params Command[] subcommands)
    {
        var root = new RootCommand("test");
        foreach (var option in GlobalOptions.All())
            root.Options.Add(option);
        foreach (var subcommand in subcommands)
            root.Subcommands.Add(subcommand);

        return root;
    }

    /// <summary>
    /// The production command tree, with no providers and a throwaway config directory. Use this
    /// when a test is about the surface itself (help, examples, the snapshot) rather than one
    /// command's parsing.
    /// </summary>
    public static RootCommand Full()
        => Program.BuildRootCommand(
            providers: [],
            new CompositeExpressionFormatterClient([]),
            version: Version,
            TestServices.Create());

    /// <summary>
    /// Every command below <paramref name="command"/>, depth-first in declaration order, each with
    /// the path of names that reaches it.
    /// </summary>
    /// <param name="includeHidden">
    /// Hidden commands are still invocable, so tests about behavior (help exit codes) include them
    /// while tests about the documented surface (the snapshot, the docs gate) do not.
    /// </param>
    public static IEnumerable<(Command Command, string[] Path)> Descendants(
        Command command, bool includeHidden, string[]? prefix = null)
    {
        foreach (var sub in command.Subcommands)
        {
            if (!includeHidden && sub.Hidden)
                continue;

            string[] path = prefix is null ? [sub.Name] : [.. prefix, sub.Name];
            yield return (sub, path);

            foreach (var nested in Descendants(sub, includeHidden, path))
                yield return nested;
        }
    }
}
