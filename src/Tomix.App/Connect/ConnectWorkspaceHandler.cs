using Tomix.App.Info;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;

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
    /// Opens the remote mirror target to classify it. <see cref="ConnectWorkspaceProbeStatus.Exists"/>
    /// carries the canonical dataset name so the stored mirror matches exactly (Power BI rejects
    /// XMLA deploys that change a dataset's name, even casing). A <c>TOMIX_DATABASE_NOT_FOUND</c>
    /// failure means the server is reachable but the database does not exist yet — OK for a new mirror.
    /// </summary>
    public async Task<ConnectWorkspaceProbeResult> ProbeAsync(
        ConnectWorkspaceProbeRequest request,
        CancellationToken cancellationToken)
    {
        var result = await new InfoModelHandler(_providers).HandleAsync(
            new InfoModelRequest(ModelReference.Remote(request.Workspace, request.Database)),
            cancellationToken);

        if (result.Success)
            return new ConnectWorkspaceProbeResult(
                ConnectWorkspaceProbeStatus.Exists,
                ConnectPlanHandler.ResolveWorkspaceDatabase(request.Database, result.Data!.Summary.DatabaseName),
                result.Diagnostics,
                result.ExitCode);

        if (result.Diagnostics.Any(d => d.Code == "TOMIX_DATABASE_NOT_FOUND"))
            return new ConnectWorkspaceProbeResult(
                ConnectWorkspaceProbeStatus.Missing, null, result.Diagnostics, result.ExitCode);

        return new ConnectWorkspaceProbeResult(
            ConnectWorkspaceProbeStatus.Unreachable, null, result.Diagnostics, result.ExitCode);
    }

    /// <summary>
    /// Scaffolds a local mirror workspace by exporting the primary model to disk. Applies only
    /// when the target is absent (or <c>Force</c> is set, which deletes an existing dir/file
    /// first). Silently reports <c>Initialized=false</c> when no provider can open the primary
    /// or its session cannot export.
    /// </summary>
    public async Task<ConnectWorkspaceInitResult> InitializeAsync(
        ConnectWorkspaceInitRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Force && (Directory.Exists(request.Workspace) || File.Exists(request.Workspace)))
            return new ConnectWorkspaceInitResult(Initialized: false, null, null);

        var serialization = string.IsNullOrWhiteSpace(request.WorkspaceFormat) ? "tmdl" : request.WorkspaceFormat.Trim();
        var target = serialization.Equals("bim", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(request.Workspace, "model.bim")
            : request.Workspace;

        var provider = _providers.FirstOrDefault(p => p.CanOpen(request.Primary));
        if (provider is null)
            return new ConnectWorkspaceInitResult(Initialized: false, null, null);

        await using var session = await provider.OpenAsync(request.Primary, cancellationToken);
        if (session is not IModelExportSession exporter)
            return new ConnectWorkspaceInitResult(Initialized: false, null, null);

        if (request.Force && (Directory.Exists(request.Workspace) || File.Exists(request.Workspace)))
        {
            if (Directory.Exists(request.Workspace))
                Directory.Delete(request.Workspace, true);
            else
                File.Delete(request.Workspace);
        }

        await exporter.ExportAsync(
            new ModelExportRequest(target, serialization, Force: true, SupportingFiles: false),
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
    bool Force,
    ModelReference Primary);

public sealed record ConnectWorkspaceInitResult(
    bool Initialized,
    string? Path,
    string? Serialization);
