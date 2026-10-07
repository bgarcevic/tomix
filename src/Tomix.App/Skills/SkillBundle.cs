using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Tomix.App.Skills;

/// <summary>
/// The agent skill shipped inside this binary: the files of <c>/skills/tomix</c>, embedded at
/// build time so an installed skill always describes the command surface of the <c>tx</c> that
/// wrote it.
/// </summary>
/// <remarks>
/// An installed copy carries a stamp in its <c>SKILL.md</c> frontmatter (the Agent Skills
/// <c>metadata</c> map): the tomix version that wrote it and a hash of the bundle's content.
/// Hashing the installed files again (stamp removed) tells an untouched copy from one the user
/// edited, and comparing the recorded hash with this bundle's tells a stale copy from a current one.
/// </remarks>
public sealed class SkillBundle
{
    public const string SkillName = "tomix";
    public const string EntryFile = "SKILL.md";

    private const string ResourcePrefix = "Tomix.App.Skills.tomix/";

    private static readonly Regex StampBlock = new(
        "^metadata:\n  tomix-version: \"(?<version>[^\"\n]*)\"\n  tomix-hash: \"(?<hash>[0-9a-f]+)\"\n",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    public SkillBundle(IReadOnlyDictionary<string, string> files)
    {
        if (!files.ContainsKey(EntryFile))
            throw new ArgumentException($"A skill bundle needs a {EntryFile}.", nameof(files));

        Files = files
            .OrderBy(file => file.Key, StringComparer.Ordinal)
            .ToDictionary(file => file.Key, file => Normalize(file.Value), StringComparer.Ordinal);
        Hash = HashOf(Files);
    }

    /// <summary>Relative path (forward slashes) to normalized (LF) content.</summary>
    public IReadOnlyDictionary<string, string> Files { get; }

    /// <summary>Content hash of the unstamped bundle.</summary>
    public string Hash { get; }

    /// <summary>The bundle embedded in this assembly.</summary>
    public static SkillBundle Embedded()
    {
        var assembly = typeof(SkillBundle).Assembly;
        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                continue;

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            // MSBuild's %(RecursiveDir) uses the build host's separator.
            files[name[ResourcePrefix.Length..].Replace('\\', '/')] = reader.ReadToEnd();
        }

        return new SkillBundle(files);
    }

    /// <summary>The files to write for an install, with the stamp added to <c>SKILL.md</c>.</summary>
    public IReadOnlyDictionary<string, string> Render(string version)
    {
        var rendered = new Dictionary<string, string>(Files, StringComparer.Ordinal);
        rendered[EntryFile] = AddStamp(Files[EntryFile], version, Hash);
        return rendered;
    }

    /// <summary>Reads the stamp from an installed <c>SKILL.md</c>; false when it has none.</summary>
    public static bool TryReadStamp(string skillMarkdown, out string version, out string hash)
    {
        var match = StampBlock.Match(Normalize(skillMarkdown));
        version = match.Success ? match.Groups["version"].Value : "";
        hash = match.Success ? match.Groups["hash"].Value : "";
        return match.Success;
    }

    /// <summary>Hash of installed files, computed the same way as <see cref="Hash"/>.</summary>
    public static string HashOfInstalled(IReadOnlyDictionary<string, string> installedFiles)
        => HashOf(installedFiles.ToDictionary(
            file => file.Key,
            file => file.Key == EntryFile
                ? StampBlock.Replace(Normalize(file.Value), "", 1)
                : Normalize(file.Value),
            StringComparer.Ordinal));

    private static string AddStamp(string skillMarkdown, string version, string hash)
    {
        // The frontmatter opens with "---\n"; the stamp goes just before the closing "---".
        var close = skillMarkdown.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (!skillMarkdown.StartsWith("---\n", StringComparison.Ordinal) || close < 0)
            throw new InvalidOperationException($"{EntryFile} has no YAML frontmatter.");

        var stamp = $"metadata:\n  tomix-version: \"{version}\"\n  tomix-hash: \"{hash}\"\n";
        return skillMarkdown.Insert(close + 1, stamp);
    }

    private static string HashOf(IReadOnlyDictionary<string, string> files)
    {
        var builder = new StringBuilder();
        foreach (var (path, content) in files.OrderBy(file => file.Key, StringComparer.Ordinal))
            builder.Append(path).Append('\0').Append(content).Append('\0');

        // 16 hex digits identify a version of a handful of text files; the full digest adds nothing.
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..16];
    }

    private static string Normalize(string content) => content.Replace("\r\n", "\n");
}
