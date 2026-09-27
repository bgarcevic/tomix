using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Connect;

/// <summary>
/// <c>tx connect --local --list</c>: reports every running Power BI Desktop instance without
/// connecting or touching the saved session — the non-interactive counterpart of the instance
/// picker, for scripts and agents.
/// </summary>
public sealed class ConnectLocalListHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly Func<IReadOnlyList<PowerBiDesktopInstance>> _discover;

    /// <param name="discover">
    /// Instance discovery; defaults to <see cref="PowerBiDesktopDiscovery.DiscoverInstances(IEnumerable{string}?)"/>.
    /// Injectable so tests stay offline.
    /// </param>
    public ConnectLocalListHandler(
        IEnumerable<IModelProvider> providers,
        Func<IReadOnlyList<PowerBiDesktopInstance>>? discover = null)
    {
        _providers = providers.ToList();
        _discover = discover ?? (() => PowerBiDesktopDiscovery.DiscoverInstances());
    }

    public async Task<TomixResult<ConnectLocalListResult>> HandleAsync(CancellationToken cancellationToken)
    {
        var instances = _discover();
        var lookups = instances.Select(instance =>
            LocalInstanceDatabaseResolver.TryResolveAsync(_providers, instance.Endpoint, cancellationToken));
        var databases = await Task.WhenAll(lookups).ConfigureAwait(false);

        var listed = instances
            .Select((instance, i) => new LocalInstanceInfo(instance.Endpoint, instance.ReportName, databases[i]))
            .ToList();
        return TomixResult<ConnectLocalListResult>.Ok(new ConnectLocalListResult(listed));
    }
}

public sealed record ConnectLocalListResult(IReadOnlyList<LocalInstanceInfo> Instances);

/// <summary>A running Desktop instance as listed by <c>tx connect --local --list</c>.</summary>
/// <param name="Endpoint"><c>localhost:&lt;port&gt;</c>; pass it to <c>tx connect</c>.</param>
/// <param name="ReportName">The Desktop window title, or null when it could not be read.</param>
/// <param name="Database">The instance's GUID database name, or null when it could not be read.</param>
public sealed record LocalInstanceInfo(string Endpoint, string? ReportName, string? Database);
