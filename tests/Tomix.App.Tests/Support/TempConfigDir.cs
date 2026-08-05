using Tomix.App.Mutations;
using Tomix.App.State;

namespace Tomix.App.Tests.Support;

/// <summary>
/// A throwaway config directory for tests that construct stores explicitly, so no test ever
/// reads or writes the developer's real <c>~/.tomix</c>. The directory exists on construction and
/// is deleted on dispose.
/// </summary>
/// <remarks>
/// The stores are cached rather than rebuilt per access. They used to be <c>get</c>-only
/// properties, so <c>config.Stores</c> handed out a different object every time it was read —
/// which reads as one store but is not one, and defeats any assertion on store identity. The
/// stores are stateless today, so this is about matching what the call site plainly implies.
/// </remarks>
public sealed class TempConfigDir : IDisposable
{
    /// <summary>The session id <see cref="Staging"/> is scoped to.</summary>
    public const string SessionId = "test-session";

    private readonly TempDir _dir = new();

    public string Path => _dir.Path;

    public CliStateStore State { get; }

    public StagingStore Staging { get; }

    /// <summary>Stores for mutation tests: temp-dir staging, no active session.</summary>
    public MutationStores Stores { get; }

    public TempConfigDir()
    {
        State = new CliStateStore(Path);
        Staging = new StagingStore(Path, SessionId);
        Stores = new MutationStores(Staging, () => null);
    }

    /// <summary>Path of a child of the config directory. Creates nothing.</summary>
    public string Combine(params string[] parts) => _dir.Combine(parts);

    /// <summary>Creates a child directory of the config directory and returns its path.</summary>
    public string CreateSubdirectory(params string[] parts) => _dir.CreateSubdirectory(parts);

    /// <summary>Stores whose sync target is <paramref name="session"/> rather than "no session".</summary>
    public MutationStores StoresFor(CliConnectionState? session) => new(Staging, () => session);

    public override string ToString() => Path;

    public void Dispose() => _dir.Dispose();
}
