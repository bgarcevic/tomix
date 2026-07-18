using Tomix.App.Mutations;
using Tomix.App.State;

namespace Tomix.App.Tests.Support;

/// <summary>
/// A throwaway config directory for tests that construct stores explicitly, so no test ever
/// reads or writes the developer's real <c>~/.tomix</c>.
/// </summary>
public sealed class TempConfigDir : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tomix-tests-{Guid.NewGuid():N}");

    public CliStateStore State => new(Path);

    public StagingStore Staging => new(Path, "test-session");

    /// <summary>Stores for mutation tests: temp-dir staging, no active session.</summary>
    public MutationStores Stores => new(Staging, () => null);

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}
