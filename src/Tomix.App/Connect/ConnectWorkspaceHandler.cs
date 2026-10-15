using Tomix.App.Diagnostics;
using Tomix.App.Models;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Connect;

/// <summary>
/// Workspace-mirror operations for <c>tx connect</c>: probing the remote mirror target
/// (exists / missing / unreachable) and initializing a local mirror folder by exporting the
/// primary model. The overwrite confirmation stays in the CLI — this handler only reports
/// what it found or did.
/// </summary>
public sealed class ConnectWorkspaceHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    public ConnectWorkspaceHandler(IEnumerable<IModelProvider> providers)
        => _providers = providers.ToList();

    /// <summary>
    /// Classifies the remote mirror target from the workspace's database list — one connect, which
    /// still proves the XMLA endpoint works, without downloading the target's model tree.
    /// <see cref="ConnectWorkspaceProbeStatus.Exists"/> carries the canonical dataset name so the
    /// stored mirror matches exactly (Power BI rejects XMLA deploys that change a dataset's name,
    /// even casing). A name the list does not show is confirmed by opening it: only
    /// <c>TOMIX_DATABASE_NOT_FOUND</c> means the database does not exist yet — OK for a new mirror —
    /// while a dataset hidden from the list (read-only permission) must not pass as missing.
    /// </summary>
    public async Task<ConnectWorkspaceProbeResult> ProbeAsync(
        ConnectWorkspaceProbeRequest request,
        CancellationToken cancellationToken)
    {
        var endpoint = ModelReference.Remote(request.Workspace);
        var catalog = _providers.OfType<IServerCatalog>().FirstOrDefault(c => c.CanList(endpoint));
        if (catalog is not null)
        {
            var listed = await ProviderConnectionGuard.RunAsync(endpoint, async () =>
                TomixResult<IReadOnlyList<ServerDatabaseInfo>>.Ok(
                    await catalog.ListDatabasesAsync(endpoint, cancellationToken).ConfigureAwait(false)));

            if (!listed.Success)
                return new ConnectWorkspaceProbeResult(
                    ConnectWorkspaceProbeStatus.Unreachable, null, listed.Diagnostics, listed.ExitCode);

            var canonical = CanonicalName(listed.Data!, request.Database);
            if (canonical is not null)
                return new ConnectWorkspaceProbeResult(
                    ConnectWorkspaceProbeStatus.Exists,
                    ConnectPlanHandler.ResolveWorkspaceDatabase(request.Database, canonical),
                    listed.Diagnostics,
                    listed.ExitCode);
        }

        return await ProbeByOpeningAsync(request, cancellationToken);
    }

    /// <summary>The listed spelling of <paramref name="requested"/>: an exact match first, then
    /// a case-insensitive one. Null when the list does not have it.</summary>
    private static string? CanonicalName(IReadOnlyList<ServerDatabaseInfo> databases, string requested)
        => databases.FirstOrDefault(d => string.Equals(d.Name, requested, StringComparison.Ordinal))?.Name
            ?? databases.FirstOrDefault(d => string.Equals(d.Name, requested, StringComparison.OrdinalIgnoreCase))?.Name;

    /// <summary>
    /// Opens the target without reading its model, for a name the list does not show (or when no
    /// provider can list the endpoint). The provider tells a missing database from one the caller
    /// cannot read; the session is closed again at once.
    /// </summary>
    private async Task<ConnectWorkspaceProbeResult> ProbeByOpeningAsync(
        ConnectWorkspaceProbeRequest request,
        CancellationToken cancellationToken)
    {
        var target = ModelReference.Remote(request.Workspace, request.Database);
        var result = await ProviderConnectionGuard.RunAsync(target, async () =>
        {
            var provider = _providers.ResolveSingleProvider(target);
            if (provider is null)
                return TomixResult<bool>.Fail(
                    "TOMIX_NO_PROVIDER", $"No provider can open '{target.Value}'.", exitCode: 2);

            await using var session = await provider.OpenAsync(target, cancellationToken).ConfigureAwait(false);
            return TomixResult<bool>.Ok(true);
        });

        if (result.Success)
            return new ConnectWorkspaceProbeResult(
                ConnectWorkspaceProbeStatus.Exists, request.Database, result.Diagnostics, result.ExitCode);

        var status = result.Diagnostics.Any(d => d.Code == "TOMIX_DATABASE_NOT_FOUND")
            ? ConnectWorkspaceProbeStatus.Missing
            : ConnectWorkspaceProbeStatus.Unreachable;
        return new ConnectWorkspaceProbeResult(status, null, result.Diagnostics, result.ExitCode);
    }

    /// <summary>
    /// Scaffolds a local mirror workspace by exporting the primary model to disk. Applies only
    /// when the target is absent (or <c>Overwrite</c> is set, which deletes an existing dir/file
    /// first). Silently reports <c>Initialized=false</c> when no provider can open the primary
    /// or its session cannot export.
    /// </summary>
    public async Task<ConnectWorkspaceInitResult> InitializeAsync(
        ConnectWorkspaceInitRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Overwrite && (Directory.Exists(request.Workspace) || File.Exists(request.Workspace)))
            return new ConnectWorkspaceInitResult(Initialized: false, null, null);

        var serialization = string.IsNullOrWhiteSpace(request.WorkspaceFormat) ? "tmdl" : request.WorkspaceFormat.Trim();
        var target = serialization.Equals("bim", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(request.Workspace, "model.bim")
            : request.Workspace;

        var provider = _providers.ResolveSingleProvider(request.Primary);
        if (provider is null)
            return new ConnectWorkspaceInitResult(Initialized: false, null, null);

        await using var session = await provider.OpenAsync(request.Primary, cancellationToken);
        if (session is not IModelExportSession exporter)
            return new ConnectWorkspaceInitResult(Initialized: false, null, null);

        if (request.Overwrite && (Directory.Exists(request.Workspace) || File.Exists(request.Workspace)))
        {
            if (Directory.Exists(request.Workspace))
                Directory.Delete(request.Workspace, true);
            else
                File.Delete(request.Workspace);
        }

        await exporter.ExportAsync(
            new ModelExportRequest(target, serialization, Overwrite: true, SupportingFiles: false),
            cancellationToken);

        return new ConnectWorkspaceInitResult(Initialized: true, request.Workspace, serialization);
    }
}

public sealed record ConnectWorkspaceProbeRequest(string Workspace, string Database);

public enum ConnectWorkspaceProbeStatus
{
    /// <summary>Target database exists; <c>ResolvedDatabase</c> carries its canonical name.</summary>
    Exists,

    /// <summary>Server reachable, target database does not exist yet — OK for a new mirror.</summary>
    Missing,

    /// <summary>The workspace server could not be reached; diagnostics carry the detail.</summary>
    Unreachable
}

public sealed record ConnectWorkspaceProbeResult(
    ConnectWorkspaceProbeStatus Status,
    string? ResolvedDatabase,
    IReadOnlyList<TomixDiagnostic> Diagnostics,
    int ExitCode);

public sealed record ConnectWorkspaceInitRequest(
    string Workspace,
    string? WorkspaceFormat,
    bool Overwrite,
    ModelReference Primary);

public sealed record ConnectWorkspaceInitResult(
    bool Initialized,
    string? Path,
    string? Serialization);
