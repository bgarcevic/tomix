using Tomix.App;

namespace Tomix.Cli.Tests;

/// <summary>
/// <see cref="AppServices"/> rooted in a throwaway temp directory so command tests never read
/// the developer's real <c>~/.tomix</c>. Construction creates no files; tests that only parse
/// or render leave the directory nonexistent.
/// </summary>
internal static class TestServices
{
    public static AppServices Create() =>
        AppServices.Create(Path.Combine(Path.GetTempPath(), $"tomix-cli-tests-{Guid.NewGuid():N}"));
}
