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

    /// <summary>The model <paramref name="model"/> or <paramref name="server"/> and
    /// <paramref name="database"/> name, for <c>tx serve</c>'s <c>session.open</c>; a local path
    /// is made absolute.</summary>
    public ModelReference Resolve(string? model, string? server, string? database)
    {
        if (!string.IsNullOrWhiteSpace(model) && !ModelReference.IsRemoteEndpoint(model))
            model = Path.GetFullPath(model);
        return new ActiveModelResolver(state).ResolveReference(model, database, server);
    }

    /// <summary>Loads <paramref name="reference"/>, or reports why it cannot and returns the exit code.</summary>
    public async Task<(ILiveModelSession? Session, int ExitCode)> OpenAsync(
        ParseResult parseResult, ModelReference reference, CancellationToken cancellationToken)
    {
        var format = GlobalOptions.OutputFormatValue(parseResult);
        var quiet = parseResult.GetValue(GlobalOptions.Quiet) || OutputFormats.IsJson(format);
        var (session, failure) = await TryOpenAsync(reference, showSpinner: !quiet, cancellationToken);
        if (failure is not null)
            ErrorOutput.Write([failure], GlobalOptions.ErrorFormatValue(parseResult));
        return (session, failure is null ? 0 : 2);
    }

    /// <summary>Loads <paramref name="reference"/>, or returns why it cannot (exit code 2).</summary>
    public async Task<(ILiveModelSession? Session, TomixDiagnostic? Failure)> TryOpenAsync(
        ModelReference reference, bool showSpinner, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference.Value))
            return Fail("TOMIX_SESSION_NO_MODEL", "No model to open.", "Name one: connect ./model (a TMDL folder or .bim file).");

        if (staging.TryLoad(reference) is not null)
            return Fail(
                "TOMIX_STAGE_PENDING",
                $"{reference.Value} has staged changes; a session does not pick them up.",
                "Run 'tx stage commit' or 'tx stage discard' first.");

        var provider = providers.ResolveSingleProvider(reference);
        if (provider is null)
            return Fail("TOMIX_NO_PROVIDER", $"No provider can open model: {reference.Value}", "Pass a TMDL folder or a .bim file.");
        if (provider is not ILiveModelProvider live)
            return Fail(
                "TOMIX_SESSION_SOURCE_UNSUPPORTED",
                $"A session cannot open {reference.Value} yet; it opens TMDL folders and .bim files.",
                "Save the model locally with 'tx save -o <folder>' and open that.");

        var session = await CliSpinner.RunAsync(
            "Loading model...",
            () => live.OpenLiveAsync(reference, cancellationToken),
            suppress: !showSpinner);
        return (session, null);
    }

    private static (ILiveModelSession?, TomixDiagnostic) Fail(string code, string message, string hint)
        => (null, new TomixDiagnostic(code, DiagnosticSeverity.Error, message, hint));
}
