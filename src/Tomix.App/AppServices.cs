using Tomix.App.Bpa;
using Tomix.App.Config;
using Tomix.App.Mutations;
using Tomix.App.State;
using Tomix.App.Update;
using Tomix.Core.Configuration;

namespace Tomix.App;

/// <summary>
/// The stateful, filesystem-backed services built once by the composition root
/// (<c>Program.Main</c>) and threaded into commands and handlers. Construction is
/// side-effect-free: every store only computes paths; no directory is created until a
/// store is first written. <c>TOMIX_CONFIG_DIR</c> and <c>TOMIX_SESSION</c> are therefore
/// read once per process instead of on every ambient <c>new</c>.
/// </summary>
public sealed record AppServices(
    string ConfigDirectory,
    string ConfigFilePath,
    CliStateStore State,
    StagingStore Staging,
    TomixConfigStore ConfigStore,
    BpaUserRuleState BpaRules,
    UpdateCheckStore UpdateCheck)
{
    public static AppServices Create(string? configDirectory = null)
    {
        var dir = configDirectory ?? TomixPaths.ConfigDirectory;
        var configFile = TomixPaths.ConfigFileIn(dir);
        var state = new CliStateStore(dir);

        return new AppServices(
            dir,
            configFile,
            state,
            new StagingStore(dir, state.CurrentSessionId),
            new TomixConfigStore(configFile),
            new BpaUserRuleState(dir),
            new UpdateCheckStore(dir));
    }

    /// <summary>The active session, read from disk at call time (never cached).</summary>
    public CliConnectionState? LoadCurrentSession() => State.LoadCurrentSession();

    public MutationStores Mutations => new(Staging, LoadCurrentSession);
}
