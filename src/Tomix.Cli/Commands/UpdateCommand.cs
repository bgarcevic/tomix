using System.CommandLine;
using System.Runtime.InteropServices;
using Spectre.Console;
using Tomix.App.Update;
using Tomix.Cli.Output;
using Tomix.Core.Results;
using Tomix.Core.Update;

namespace Tomix.Cli.Commands;

internal sealed class UpdateCommand : ICommandModule
{
    private readonly string _version;
    private readonly IReleaseSource _releaseSource;
    private readonly UpdateCheckStore _updateCheck;

    public UpdateCommand(string version, IReleaseSource releaseSource, UpdateCheckStore updateCheck)
    {
        _version = version;
        _releaseSource = releaseSource;
        _updateCheck = updateCheck;
    }

    public Command Build()
    {
        var check = new Option<bool>("--check")
        {
            Description = "Preview only: show the latest version and release notes without changing anything."
        };
        var targetVersion = new Option<string?>("--version")
        {
            Description = "Update (or downgrade, with --yes) to a specific released version instead of the latest."
        };
        var format = OutputFormats.CreateOption();

        var command = new Command("update", "Update tx to the latest release.")
        {
            check,
            targetVersion,
            format
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var formatValue = parseResult.GetValue(format) ?? OutputFormats.Text;
            if (!CommandOutput.TryValidateFormat(parseResult, formatValue, "update", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var installKind = InstallationInspector.Detect();
            var checkHandler = new UpdateCheckHandler(_releaseSource, _updateCheck);
            var target = parseResult.GetValue(targetVersion);
            var checkResult = await checkHandler.HandleAsync(_version, installKind, target, cancellationToken);

            if (parseResult.GetValue(check) || checkResult.Data is null)
            {
                return CommandOutput.Render(
                    checkResult, formatValue, parseResult.GetValue(GlobalOptions.ErrorFormat), UpdateRenderer.RenderCheck);
            }

            return await ApplyAsync(parseResult, formatValue, checkResult.Data, target, installKind, cancellationToken);
        });

        return command;
    }

    private async Task<int> ApplyAsync(
        ParseResult parseResult,
        string formatValue,
        UpdateCheckResult checkData,
        string? pinnedVersion,
        InstallKind installKind,
        CancellationToken cancellationToken)
    {
        var resolvedTarget = checkData.LatestVersion!;

        // Pinning an older version is a downgrade: allowed, but never silently.
        var isDowngrade = pinnedVersion is not null
            && CliVersion.TryParse(resolvedTarget, out var targetParsed)
            && CliVersion.TryParse(_version, out var currentParsed)
            && currentParsed.IsNewerThan(targetParsed);

        if (!checkData.UpdateAvailable && !isDowngrade)
        {
            var upToDate = TomixResult<UpdateApplyResult>.Ok(new UpdateApplyResult(
                PreviousVersion: _version,
                NewVersion: _version,
                InstallKind: installKind,
                Method: "none"));
            return CommandOutput.Render(upToDate, formatValue, _ =>
                AnsiConsole.MarkupLine(Styling.Success($"tx is up to date ({_version}).")));
        }

        if (installKind is InstallKind.Development or InstallKind.Unknown)
        {
            var unsupported = TomixResult<UpdateApplyResult>.Fail(
                code: "TOMIX_UPDATE_UNSUPPORTED_INSTALL",
                message: $"This tx was not installed in an updatable way (detected: {installKind}).",
                exitCode: 2,
                hint: "Reinstall via install.sh/install.ps1 or 'dotnet tool install -g Tomix.Cli'; the dev wrapper always runs current source.");
            return CommandOutput.Render(
                unsupported, formatValue, parseResult.GetValue(GlobalOptions.ErrorFormat), _ => { });
        }

        var action = isDowngrade ? "Downgrade" : "Update";
        if (!ConfirmationHelper.ConfirmOrAbort(
            action,
            $"tx {_version} -> {resolvedTarget}",
            parseResult.GetValue(GlobalOptions.Yes),
            parseResult.GetValue(GlobalOptions.NonInteractive)))
        {
            return 1;
        }

        var applyHandler = new UpdateApplyHandler(_releaseSource, new SystemProcessRunner());
        var applyResult = await applyHandler.HandleAsync(
            new UpdateApplyRequest(
                CurrentVersion: _version,
                TargetVersion: resolvedTarget,
                Kind: installKind,
                ProcessPath: Environment.ProcessPath,
                RuntimeIdentifier: RuntimeInformation.RuntimeIdentifier),
            cancellationToken);

        return CommandOutput.Render(
            applyResult, formatValue, parseResult.GetValue(GlobalOptions.ErrorFormat), UpdateRenderer.RenderApply);
    }
}
