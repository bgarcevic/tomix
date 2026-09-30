using System.CommandLine;
using Spectre.Console;
using Tomix.App.Get;
using Tomix.Cli.Output;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;

namespace Tomix.Cli.Commands;

/// <summary>
/// Everything a read needs once a command module has parsed its own options. <c>get</c> fills it
/// from its full flag set; the <c>ls</c> and <c>deps</c> shortcuts fill it with their mode fixed.
/// </summary>
internal sealed record GetInvocation(
    string CommandName,
    string? Path,
    GetMode Mode = GetMode.Auto,
    string? Query = null,
    string? TypeValue = null,
    bool All = false,
    IReadOnlyList<string>? Where = null,
    DepsDirection Direction = DepsDirection.Both,
    bool Deep = false,
    int MaxDepth = 10,
    bool HiddenOnly = false,
    bool PathsOnly = false,
    bool NoMultiline = false);

/// <summary>
/// Resolves the model a read runs against. It may also rewrite the path: <c>ls</c> accepts its
/// legacy <c>ls &lt;model&gt; [path-filter]</c> order, which is only knowable once the model is.
/// </summary>
internal delegate bool ModelResolver(string? path, out ModelReference reference, out string? resolvedPath, out int exitCode);

/// <summary>
/// The CLI half of the single read pipeline: validates the invocation for the mode it resolves
/// to, runs <see cref="GetModelHandler"/>, and renders the result for that mode. <c>get</c>,
/// <c>ls</c> and <c>deps</c> all end here, so their output cannot drift.
/// </summary>
internal static class GetPipeline
{
    private static readonly string[] ObjectFormats =
        [OutputFormats.Text, OutputFormats.Json, OutputFormats.Csv, OutputFormats.Tmdl, OutputFormats.Bim, OutputFormats.Tmsl];

    private static readonly string[] ListFormats = [OutputFormats.Text, OutputFormats.Json, OutputFormats.Csv];

    private static readonly string[] DepsFormats = [OutputFormats.Text, OutputFormats.Json];

    public static async Task<int> RunAsync(
        ParseResult parseResult,
        IReadOnlyList<IModelProvider> providers,
        GetInvocation invocation,
        ModelResolver resolveModel,
        CancellationToken cancellationToken)
    {
        var formatValue = GlobalOptions.OutputFormatValue(parseResult);
        var errorFormat = GlobalOptions.ErrorFormatValue(parseResult, formatValue);

        var filters = new List<PropertyFilter>();
        foreach (var text in invocation.Where ?? [])
        {
            if (!PropertyFilter.TryParse(text, out var filter))
                return UsageError(
                    errorFormat,
                    "TOMIX_INVALID_WHERE",
                    $"Invalid --where value '{text}'.",
                    "Use Prop=Value, for example --where Name=*margin* or --where IsHidden=true.");
            filters.Add(filter);
        }

        // Settled from the path alone, before the model opens, so an unsupported format or a
        // misplaced option fails fast instead of after a slow load.
        var mode = GetModelHandler.ResolveMode(new GetModelRequest(
            new ModelReference(""), invocation.Path, invocation.Query, null, invocation.Mode, filters));

        if (!CommandOutput.TryValidateFormat(parseResult, formatValue, FormatScope(invocation.CommandName, mode), SupportedFormats(mode)))
            return 2;

        ModelObjectKind? type = null;
        if (!string.IsNullOrWhiteSpace(invocation.TypeValue))
        {
            if (!ModelObjectKindParser.TryParse(invocation.TypeValue, out var parsed))
                return TypeValidation.WriteInvalidTypeError(errorFormat);
            type = parsed;
        }

        if (!resolveModel(invocation.Path, out var reference, out var path, out var resolveExit))
            return resolveExit;

        var quiet = parseResult.GetValue(GlobalOptions.Quiet);
        var request = new GetModelRequest(
            reference, path, invocation.Query, type, mode, filters,
            invocation.Direction, invocation.Deep, invocation.MaxDepth, invocation.HiddenOnly);
        var tracesDeps = mode is GetMode.Deps or GetMode.Unused;

        var result = await CliSpinner.RunAsync(
            tracesDeps ? "Analyzing dependencies..." : "Loading model...",
            () => new GetModelHandler(providers).HandleAsync(request, cancellationToken),
            suppress: quiet || OutputFormats.IsJson(formatValue) || OutputFormats.IsCsv(formatValue));

        var showUpstream = invocation.Direction != DepsDirection.Downstream;
        var showDownstream = invocation.Direction != DepsDirection.Upstream;

        return CommandOutput.Render<GetModelResult, object?>(
            result,
            formatValue,
            data =>
            {
                switch (data.Mode)
                {
                    case GetMode.List:
                        LsRenderer.Render(data.List!, invocation.PathsOnly, invocation.NoMultiline);
                        break;
                    case GetMode.Deps or GetMode.Unused:
                        DepsRenderer.Render(data.Deps!, showUpstream, showDownstream, invocation.Deep, quiet);
                        break;
                    default:
                        GetRenderer.Render(data.Object!, formatValue, invocation.All);
                        if (!quiet && OutputFormats.IsTextLike(formatValue))
                            GetRenderer.RenderHint(data.Object!, invocation.All);
                        break;
                }
            },
            data => data.Mode switch
            {
                GetMode.List => LsRenderer.ToReferenceJson(data.List!),
                GetMode.Deps or GetMode.Unused => DepsRenderer.ToReferenceJson(data.Deps!, showUpstream, showDownstream),
                _ => GetRenderer.ToReferenceJson(data.Object!)
            },
            data =>
            {
                if (data.Mode == GetMode.List)
                    LsRenderer.RenderCsv(data.List!);
                else
                    GetRenderer.RenderCsv(data.Object!);
            },
            errorFormat);
    }

    /// <summary>Writes a usage diagnostic and returns exit code 2.</summary>
    public static int UsageError(string? errorFormat, string code, string message, string hint)
    {
        ErrorOutput.Write([new TomixDiagnostic(code, DiagnosticSeverity.Error, message, hint)], errorFormat);
        return 2;
    }

    private static string[] SupportedFormats(GetMode mode) => mode switch
    {
        GetMode.List => ListFormats,
        GetMode.Deps or GetMode.Unused => DepsFormats,
        _ => ObjectFormats
    };

    /// <summary>
    /// Names what was asked for in a format error: on <c>get</c> the mode matters, since
    /// <c>tx get Sales --output-format tmdl</c> works but a listing (a wildcard or container
    /// path counts as <c>--ls</c>) cannot be emitted as TMDL.
    /// </summary>
    private static string FormatScope(string commandName, GetMode mode) => commandName != "get"
        ? commandName
        : mode switch
        {
            GetMode.List => "get --ls",
            GetMode.Deps => "get --deps",
            GetMode.Unused => "get --unused",
            _ => "get"
        };
}
