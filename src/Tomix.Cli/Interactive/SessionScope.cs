using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using Tomix.App.Models;
using Tomix.App.State;
using Tomix.Cli.Commands;
using Tomix.Core.Models;

namespace Tomix.Cli.Interactive;

/// <summary>
/// The live session that <c>tx interactive</c> runs its commands in. A command module built with a
/// scope leases its model from the session instead of opening it, and a command that names no
/// model gets the session's.
/// </summary>
internal sealed class SessionScope(LiveSessionSource source)
{
    public LiveSessionSource Source { get; } = source;

    public ILiveModelSession Session => Source.Session;

    public ModelReference Model => Source.Session.Reference;

    /// <summary>Where a command module leases its model: the session when it has one, otherwise the providers.</summary>
    public static IModelSessionSource SourceFor(SessionScope? scope, IReadOnlyList<IModelProvider> providers)
        => scope is null ? new OneShotSessionSource(providers) : scope.Source;

    /// <summary>
    /// <see cref="RecentConnections.TryResolveModel"/>, except that inside a session a command that
    /// names no model (no path, <c>--server</c>, <c>--database</c> or <c>--recent</c>) addresses the
    /// session's model rather than the active connection.
    /// </summary>
    public static bool TryResolveModel(
        SessionScope? scope,
        ParseResult parseResult,
        string? explicitModel,
        CliStateStore store,
        [NotNullWhen(true)] out ModelReference? reference,
        out int exitCode)
    {
        if (scope is not null
            && string.IsNullOrWhiteSpace(explicitModel)
            && string.IsNullOrWhiteSpace(parseResult.GetValue(GlobalOptions.Server))
            && string.IsNullOrWhiteSpace(parseResult.GetValue(GlobalOptions.Database))
            && !GlobalOptions.RecentSpecified(parseResult))
        {
            reference = scope.Model;
            exitCode = 0;
            return true;
        }

        return RecentConnections.TryResolveModel(parseResult, explicitModel, store, out reference, out exitCode);
    }
}
