using Tomix.App.Mutations;
using Tomix.Core.Authentication;
using Tomix.Core.Models;
using Tomix.Core.Results;
using Tomix.Core.Vertipaq;

namespace Tomix.App.Vertipaq;

public sealed class VertipaqHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly IVertipaqAnalyzer _analyzer;
    private readonly MutationStores _stores;

    public VertipaqHandler(IEnumerable<IModelProvider> providers, IVertipaqAnalyzer analyzer, MutationStores stores)
    {
        _providers = providers.ToList();
        _analyzer = analyzer;
        _stores = stores;
    }

    public async Task<TomixResult<VertipaqResult>> HandleAsync(
        VertipaqRequest request,
        CancellationToken cancellationToken)
    {
        if (Conflict(request) is { } conflict)
            return TomixResult<VertipaqResult>.Fail(
                "TOMIX_VERTIPAQ_OPTIONS_CONFLICT", conflict, exitCode: 2);

        string analyzedSource;
        var usedRemoteFallback = false;
        VertipaqModelStats stats;
        string? exportedPath = null;
        string? dictionaryPath = null;

        if (!string.IsNullOrWhiteSpace(request.ImportPath))
        {
            analyzedSource = request.ImportPath;
            try
            {
                stats = await _analyzer.ImportAsync(request.ImportPath, cancellationToken);
            }
            catch (VertipaqAnalysisException ex)
            {
                return TomixResult<VertipaqResult>.Fail(
                    "TOMIX_VPAX_READ_FAILED", ex.Message, exitCode: 2,
                    hint: "Pass a file created with 'tx vertipaq --export <file.vpax>'.");
            }
        }
        else
        {
            // Storage statistics only exist on a live engine. When the resolved model is a local
            // definition but the session has a remote side (workspace mode), analyze that instead;
            // --annotate below still mutates the primary so workspace mirroring works as usual.
            ModelReference liveModel;
            if (request.Model.IsRemote)
            {
                liveModel = request.Model;
            }
            else if (request.RemoteSyncTarget is { IsRemote: true } remote)
            {
                liveModel = remote;
                usedRemoteFallback = true;
            }
            else if (string.IsNullOrWhiteSpace(request.Model.Value))
            {
                return TomixResult<VertipaqResult>.Fail(
                    "TOMIX_NO_MODEL",
                    "No model specified and no active connection.",
                    exitCode: 2,
                    hint: "Connect first (tx connect -s <workspace> -d <model>), pass --server/--database, or analyze a file with --import <file.vpax>.");
            }
            else
            {
                return TomixResult<VertipaqResult>.Fail(
                    "TOMIX_VERTIPAQ_UNSUPPORTED_SOURCE",
                    $"VertiPaq statistics require a live engine; '{request.Model.Value}' is a local model definition.",
                    exitCode: 2,
                    hint: "Connect to a deployed model (tx connect -s <workspace> -d <model>) or analyze an exported file with --import <file.vpax>.");
            }

            analyzedSource = liveModel.Value;

            try
            {
                if (!string.IsNullOrWhiteSpace(request.ExportPath))
                {
                    var export = await _analyzer.ExportAsync(
                        liveModel, request.ExportPath, request.Obfuscate, cancellationToken);
                    stats = export.Stats;
                    exportedPath = export.VpaxPath;
                    dictionaryPath = export.ObfuscationDictionaryPath;
                }
                else
                {
                    stats = await _analyzer.AnalyzeAsync(liveModel, cancellationToken);
                }
            }
            catch (AuthenticationRequiredException ex)
            {
                return TomixResult<VertipaqResult>.Fail(
                    "TOMIX_AUTH_REQUIRED", ex.Message, exitCode: 1,
                    hint: "Run 'tx auth login' to authenticate, or use --auth spn for service principal.");
            }
            catch (VertipaqAnalysisException ex) when (ex.Kind == VertipaqAnalysisKind.VpaxWriteFailed)
            {
                return TomixResult<VertipaqResult>.Fail(
                    "TOMIX_VPAX_WRITE_FAILED", ex.Message, exitCode: 2,
                    hint: "Check that the target directory exists and is writable.");
            }
            catch (VertipaqAnalysisException ex)
            {
                return TomixResult<VertipaqResult>.Fail(
                    "TOMIX_VERTIPAQ_FAILED", ex.Message, exitCode: 1,
                    hint: "Verify the endpoint, database name, and your permissions on the model.");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.TableFilter))
        {
            var filtered = FilterToTable(stats, request.TableFilter);
            if (filtered is null)
                return TomixResult<VertipaqResult>.Fail(
                    "TOMIX_VERTIPAQ_TABLE_NOT_FOUND",
                    $"Table not found: {request.TableFilter}",
                    exitCode: 1,
                    hint: TableNotFoundHint(stats, request.TableFilter));

            stats = filtered;
        }

        if (!request.Annotate)
            return TomixResult<VertipaqResult>.Ok(new VertipaqResult(
                stats, analyzedSource, usedRemoteFallback, exportedPath, dictionaryPath, Annotate: null));

        var annotate = await AnnotateAsync(request, stats, cancellationToken);
        if (!annotate.Success || annotate.Data is null)
            return TomixResult<VertipaqResult>.Fail(
                annotate.Diagnostics.FirstOrDefault()?.Code ?? "TOMIX_MUTATION_FAILED",
                annotate.Diagnostics.FirstOrDefault()?.Message ?? "Annotation failed.",
                exitCode: annotate.ExitCode == 0 ? 1 : annotate.ExitCode);

        return TomixResult<VertipaqResult>.Ok(
            new VertipaqResult(
                stats, analyzedSource, usedRemoteFallback, exportedPath, dictionaryPath, annotate.Data),
            annotate.ExitCode);
    }

    private Task<TomixResult<VertipaqAnnotateResult>> AnnotateAsync(
        VertipaqRequest request,
        VertipaqModelStats stats,
        CancellationToken cancellationToken)
    {
        var options = new MutationOptions(
            request.Save, SaveTo: null, Stage: false, Revert: false, Serialization: "", Force: true);

        return MutationRunner.RunAsync(
            _providers, request.Model, options, "vertipaq", _stores,
            (mutator, _, _) =>
            {
                var annotated = 0;
                var skipped = 0;

                foreach (var target in VertipaqAnnotationBuilder.Build(stats))
                {
                    try
                    {
                        mutator.SetProperty(new ModelObjectSetRequest(target.Path, target.Assignments, target.Type));
                        annotated++;
                    }
                    catch (Exception ex) when (ex is ObjectNotFoundException or ArgumentException)
                    {
                        // Statistics may cover engine-side objects the mutated model doesn't have
                        // (auto date tables, mirror drift), and a few names are unaddressable as
                        // paths (e.g. relationship endpoints with apostrophes); skip those
                        // instead of failing the run.
                        skipped++;
                    }
                }

                return Task.FromResult<(bool, string, Func<MutationOutcome, VertipaqAnnotateResult>)>((
                    annotated > 0,
                    $"vertipaq annotate {annotated} objects",
                    outcome => new VertipaqAnnotateResult(
                        annotated, skipped, outcome.Saved, outcome.Synced, outcome.SyncTarget, outcome.SyncWarning)));
            },
            new VertipaqAnnotateResult(0, 0, Saved: false),
            cancellationToken);
    }

    private static string? Conflict(VertipaqRequest request)
    {
        var import = !string.IsNullOrWhiteSpace(request.ImportPath);

        if (import && !string.IsNullOrWhiteSpace(request.ExportPath))
            return "--import and --export are mutually exclusive.";
        if (import && request.Annotate)
            return "--annotate needs a model to write to; it cannot be combined with --import.";
        if (request.Obfuscate && string.IsNullOrWhiteSpace(request.ExportPath))
            return "--obfuscate requires --export <file.vpax>.";
        if (request.Save && !request.Annotate)
            return "--save persists annotations; it requires --annotate.";

        return null;
    }

    private static VertipaqModelStats? FilterToTable(VertipaqModelStats stats, string tableFilter)
    {
        var table = stats.Tables.FirstOrDefault(
            t => string.Equals(t.TableName, tableFilter, StringComparison.OrdinalIgnoreCase));
        if (table is null)
            return null;

        var name = table.TableName;
        return stats with
        {
            Tables = [table],
            Columns = stats.Columns.Where(c => c.TableName == name).ToList(),
            Partitions = stats.Partitions.Where(p => p.TableName == name).ToList(),
            Relationships = stats.Relationships.Where(r => r.FromTable == name || r.ToTable == name).ToList()
        };
    }

    private static string TableNotFoundHint(VertipaqModelStats stats, string filter)
    {
        var close = stats.Tables
            .Select(t => t.TableName)
            .Where(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Take(5)
            .ToList();

        if (close.Count == 0)
            close = stats.Tables.Select(t => t.TableName).Take(5).ToList();

        return close.Count == 0
            ? "The model contains no tables."
            : $"Known tables include: {string.Join(", ", close)}.";
    }
}
