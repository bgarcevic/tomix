using Tomix.Core.Results;
using Tomix.Core.Update;

namespace Tomix.App.Update;

/// <param name="CurrentVersion">The running CLI's version.</param>
/// <param name="TargetVersion">Resolved target (never null — the CLI resolves latest before applying).</param>
/// <param name="ProcessPath">From <c>Environment.ProcessPath</c>; required for the standalone swap.</param>
/// <param name="RuntimeIdentifier">From <c>RuntimeInformation.RuntimeIdentifier</c>; picks the release asset.</param>
public sealed record UpdateApplyRequest(
    string CurrentVersion,
    string TargetVersion,
    InstallKind Kind,
    string? ProcessPath,
    string RuntimeIdentifier);

/// <summary>
/// Performs the update for the detected install kind: <c>dotnet tool update</c> for global
/// tools, checksum-verified binary swap for standalone installs.
/// </summary>
public sealed class UpdateApplyHandler
{
    private readonly IReleaseSource _source;
    private readonly IProcessRunner _processRunner;

    public UpdateApplyHandler(IReleaseSource source, IProcessRunner processRunner)
    {
        _source = source;
        _processRunner = processRunner;
    }

    public async Task<TomixResult<UpdateApplyResult>> HandleAsync(
        UpdateApplyRequest request,
        CancellationToken cancellationToken)
        => request.Kind switch
        {
            InstallKind.DotnetTool => await UpdateDotnetToolAsync(request, cancellationToken).ConfigureAwait(false),
            InstallKind.Standalone => await SwapBinaryAsync(request, cancellationToken).ConfigureAwait(false),
            _ => TomixResult<UpdateApplyResult>.Fail(
                code: "TOMIX_UPDATE_UNSUPPORTED_INSTALL",
                message: $"This tx was not installed in an updatable way (detected: {request.Kind}).",
                exitCode: 2,
                hint: "Reinstall via install.sh/install.ps1 or 'dotnet tool install -g Tomix.Cli'; the dev wrapper always runs current source.")
        };

    private async Task<TomixResult<UpdateApplyResult>> UpdateDotnetToolAsync(
        UpdateApplyRequest request,
        CancellationToken cancellationToken)
    {
        ProcessRunResult run;
        try
        {
            run = await _processRunner.RunAsync(
                "dotnet",
                ["tool", "update", "-g", "Tomix.Cli", "--version", request.TargetVersion],
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return TomixResult<UpdateApplyResult>.Fail(
                code: "TOMIX_UPDATE_TOOL_FAILED",
                message: $"Could not run 'dotnet tool update': {ex.Message}",
                exitCode: 1,
                hint: "Run 'dotnet tool update -g Tomix.Cli' manually.");
        }

        if (run.ExitCode != 0)
        {
            return TomixResult<UpdateApplyResult>.Fail(
                code: "TOMIX_UPDATE_TOOL_FAILED",
                message: $"'dotnet tool update' exited with code {run.ExitCode}: {Tail(run.StandardError)}",
                exitCode: 1,
                hint: "Close other tx processes and retry, or run 'dotnet tool update -g Tomix.Cli' manually.");
        }

        return TomixResult<UpdateApplyResult>.Ok(new UpdateApplyResult(
            PreviousVersion: request.CurrentVersion,
            NewVersion: request.TargetVersion,
            InstallKind: InstallKind.DotnetTool,
            Method: "dotnet-tool"));
    }

    private async Task<TomixResult<UpdateApplyResult>> SwapBinaryAsync(
        UpdateApplyRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ProcessPath))
        {
            return TomixResult<UpdateApplyResult>.Fail(
                code: "TOMIX_UPDATE_APPLY_FAILED",
                message: "Cannot locate the running tx binary to replace.",
                exitCode: 1);
        }

        var assetName = BinaryUpdater.AssetNameFor(request.RuntimeIdentifier);

        byte[] asset;
        string checksums;
        try
        {
            asset = await _source.DownloadAssetAsync(request.TargetVersion, assetName, cancellationToken).ConfigureAwait(false);
            checksums = await _source.DownloadChecksumsAsync(request.TargetVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return TomixResult<UpdateApplyResult>.Fail(
                code: "TOMIX_UPDATE_DOWNLOAD_FAILED",
                message: $"Could not download {assetName} for {request.TargetVersion}: {ex.Message}",
                exitCode: 1,
                hint: "Check connectivity to github.com and that the release publishes an asset for this platform.");
        }

        if (!BinaryUpdater.VerifyChecksum(asset, checksums, assetName))
        {
            return TomixResult<UpdateApplyResult>.Fail(
                code: "TOMIX_UPDATE_CHECKSUM_MISMATCH",
                message: $"Checksum verification failed for {assetName}; the download was discarded.",
                exitCode: 1,
                hint: "Retry, or reinstall via install.sh/install.ps1.");
        }

        try
        {
            var binary = BinaryUpdater.ExtractBinary(asset, request.RuntimeIdentifier);
            BinaryUpdater.SwapInPlace(request.ProcessPath, binary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return TomixResult<UpdateApplyResult>.Fail(
                code: "TOMIX_UPDATE_APPLY_FAILED",
                message: $"Could not replace {request.ProcessPath}: {ex.Message}",
                exitCode: 1,
                hint: "Check write permissions on the install directory, or reinstall via install.sh/install.ps1.");
        }

        return TomixResult<UpdateApplyResult>.Ok(new UpdateApplyResult(
            PreviousVersion: request.CurrentVersion,
            NewVersion: request.TargetVersion,
            InstallKind: InstallKind.Standalone,
            Method: "binary-swap"));
    }

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return "(no output)";

        var lines = trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines[^Math.Min(3, lines.Length)..]);
    }
}
