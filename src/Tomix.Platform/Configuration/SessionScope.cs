using System.Security.Cryptography;
using System.Text;

namespace Tomix.Platform.Configuration;

/// <summary>
/// The directory an implicit CLI session is bound to: the enclosing git repository (or worktree)
/// root, otherwise the working directory itself. Scoping by directory rather than by shell process
/// keeps a connection stable across the fresh shell every agent tool call spawns, while a new
/// repository or worktree starts without one.
/// </summary>
public static class SessionScope
{
    /// <summary>
    /// The nearest ancestor of <paramref name="startDirectory"/> (inclusive) that contains a
    /// <c>.git</c> entry — a directory in a normal clone, a file in a linked worktree or submodule,
    /// so each worktree is its own scope. Falls back to <paramref name="startDirectory"/>.
    /// </summary>
    public static string FindRoot(string startDirectory)
    {
        var start = Path.TrimEndingDirectorySeparator(Path.GetFullPath(startDirectory));
        try
        {
            for (var current = new DirectoryInfo(start); current is not null; current = current.Parent)
            {
                var marker = Path.Combine(current.FullName, ".git");
                if (Directory.Exists(marker) || File.Exists(marker))
                    return Path.TrimEndingDirectorySeparator(current.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // An unreadable ancestor ends the walk; the working directory is still a valid scope.
        }

        return start;
    }

    /// <summary>
    /// A stable, filename-safe session id for <paramref name="scopeDirectory"/>:
    /// <c>dir-&lt;leaf&gt;-&lt;hash&gt;</c>. The leaf keeps <c>tx session list</c> readable; the hash
    /// of the full path keeps two same-named folders apart.
    /// </summary>
    public static string SessionIdFor(string scopeDirectory)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(scopeDirectory));

        // Windows paths are case-insensitive; hashing the raw casing would split one folder into
        // two sessions depending on how the shell spelled it.
        var key = OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..10];

        var leaf = Path.GetFileName(full);
        if (string.IsNullOrEmpty(leaf))
            leaf = "root";

        var safe = new StringBuilder(leaf.Length);
        foreach (var character in leaf.ToLowerInvariant())
            safe.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_');

        return $"dir-{safe}-{hash}";
    }
}
