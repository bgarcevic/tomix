using System.CommandLine;
using Tomix.App.Models;
using Tomix.App.State;
using Tomix.Cli.Commands;
using Tomix.Cli.Output;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;

namespace Tomix.Cli.Interactive;

/// <summary>
/// Opens a model as a live session for <c>tx interactive</c>, at start and for <c>open</c>:
/// resolves the model, refuses one with staged work, and loads it through a live provider.
/// </summary>
internal sealed class SessionOpener(IReadOnlyList<IModelProvider> providers, CliStateStore state, StagingStore staging)
{
    /// <summary>
    /// The model <paramref name="explicitModel"/>, <c>--server</c>/<c>--database</c> or
    /// <c>--recent</c> name, or else the active connection's; an empty reference when there is none.
    /// </summary>
    public bool TryResolve(ParseResult parseResult, string? explicitModel, out ModelReference reference, out bool isImplicit, out int exitCode)
    {
        if (!RecentConnections.TryGetSource(parseResult, explicitModel, state, out var source, out exitCode))
        {
            reference = new ModelReference("");
            isImplicit = false;
            return false;
        }

        reference = RecentConnections.CreateResolver(source, state).ResolveReference(source.Model, source.Database, source.Server);
        isImplicit = source.IsImplicit;
        return true;
    }

    /// <summary>The active connection's model; an empty reference when there is none.</summary>
    public ModelReference ActiveReference() => new ActiveModelResolver(state).ResolveReference(null, null, null);

    /// <summary>Loads <paramref name="reference"/>, or reports why it cannot and returns the exit code.</summary>
    public async Task<(ILiveModelSession? Session, int ExitCode)> OpenAsync(
        ParseResult parseResult, ModelReference reference, CancellationToken cancellationToken)
    {
        var errorFormat = GlobalOptions.ErrorFormatValue(parseResult);
        if (string.IsNullOrWhiteSpace(reference.Value))
            return Fail(errorFormat, "TOMIX_SESSION_NO_MODEL", "No model to open.", "Name one: connect ./model (a TMDL folder or .bim file).");

        if (staging.TryLoad(reference) is not null)
            return Fail(
                errorFormat,
                "TOMIX_STAGE_PENDING",
                $"{reference.Value} has staged changes; a session does not pick them up.",
                "Run 'tx stage commit' or 'tx stage discard' first.");

        var provider = providers.ResolveSingleProvider(reference);
        if (provider is null)
            return Fail(errorFormat, "TOMIX_NO_PROVIDER", $"No provider can open model: {reference.Value}", "Pass a TMDL folder or a .bim file.");
        if (provider is not ILiveModelProvider live)
            return Fail(
                errorFormat,
                "TOMIX_SESSION_SOURCE_UNSUPPORTED",
                $"An interactive session cannot open {reference.Value} yet; it opens TMDL folders and .bim files.",
                "Save the model locally with 'tx save -o <folder>' and open that.");

        var format = GlobalOptions.OutputFormatValue(parseResult);
        var session = await CliSpinner.RunAsync(
            "Loading model...",
            () => live.OpenLiveAsync(reference, cancellationToken),
            suppress: parseResult.GetValue(GlobalOptions.Quiet) || OutputFormats.IsJson(format));
        return (session, 0);
    }

    private static (ILiveModelSession?, int) Fail(string? errorFormat, string code, string message, string hint)
    {
        ErrorOutput.Write([new TomixDiagnostic(code, DiagnosticSeverity.Error, message, hint)], errorFormat);
        return (null, 2);
    }
}
