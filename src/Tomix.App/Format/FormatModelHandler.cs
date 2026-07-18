using Tomix.App.ModelObjects;
using Tomix.App.Mutations;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Format;

public sealed class FormatModelHandler
{
    private readonly IReadOnlyList<IModelProvider> _providers;
    private readonly IExpressionFormatterClient _formatter;
    private readonly MutationStores _stores;

    public FormatModelHandler(
        IEnumerable<IModelProvider> providers,
        IExpressionFormatterClient formatter,
        MutationStores stores)
    {
        _providers = providers.ToList();
        _formatter = formatter;
        _stores = stores;
    }

    // M2 transitional: removed once the CLI threads stores from the composition root.
    public FormatModelHandler(
        IEnumerable<IModelProvider> providers,
        IExpressionFormatterClient formatter)
        : this(providers, formatter, MutationStores.Ambient())
    {
    }

    public async Task<TomixResult<FormatModelResult>> HandleAsync(
        FormatModelRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Expression))
            return await FormatInlineAsync(request, cancellationToken);

        var options = new MutationOptions(
            request.Save, request.SaveTo, request.Stage, request.Revert,
            request.Serialization, request.Force, request.NoSync);

        if (!string.IsNullOrWhiteSpace(request.Path))
        {
            return await MutationRunner.RunAsync(
                _providers, request.Model, options, "format", _stores,
                async (mutator, session, _) =>
                {
                    var snapshot = await session.GetSnapshotAsync(cancellationToken);
                    var matches = ModelObjectLookup.Find(snapshot, request.Path!, request.Type).ToList();

                    if (matches.Count == 0)
                        throw new ObjectNotFoundException(
                            ModelObjectLookup.NotFoundMessage(request.Path!),
                            hint: "Run 'tx ls' to list available objects, or 'tx ls Sa*' to filter.");
                    if (matches.Count > 1)
                        throw new AmbiguousObjectException($"Object path matched more than one object: {request.Path}");

                    var obj = matches[0];
                    if (string.IsNullOrWhiteSpace(obj.Expression))
                        throw new InvalidOperationException($"Object has no expression to format: {obj.Path}");

                    if (!TryResolveLanguage(request.Language, obj.Kind, out var language, out var error))
                        throw new InvalidOperationException(error);

                    var formatted = await FormatExpressionAsync(request, obj.Expression!, language, cancellationToken);
                    if (!formatted.Success)
                        throw new InvalidOperationException($"Formatting failed for: {obj.Path}");

                    mutator.SetProperty(new ModelObjectSetRequest(
                        obj.Path,
                        [new ModelPropertyAssignment("expression", formatted.Formatted)],
                        obj.Kind));

                    var status = Status(obj.Expression!, formatted.Formatted);
                    return (status == "formatted", $"format {obj.Path}",
                        outcome => (FormatModelResult)new ObjectFormatResult(
                            formatted.Success, obj.Path, FormatterLanguages.DisplayName(language),
                            status, formatted.Formatted, outcome.Saved, outcome.Staged,
                            outcome.Synced, outcome.SyncTarget, outcome.SyncWarning));
                },
                (FormatModelResult)new ObjectFormatResult(false, "", "", "", "", null),
                cancellationToken);
        }

        return await MutationRunner.RunAsync(
            _providers, request.Model, options, "format", _stores,
            async (mutator, session, _) =>
            {
                var snapshot = await session.GetSnapshotAsync(cancellationToken);

                if (!TryResolveLanguage(request.Language, request.Type, out var language, out var error))
                    throw new InvalidOperationException(error);

                var objects = FormatTargets(snapshot, language, request.Type).ToList();
                var results = new List<ModelFormatObjectResult>();
                var successful = new List<(ModelObject Object, string Formatted)>();
                var formattedCount = 0;
                var unchangedCount = 0;
                var failedCount = 0;

                foreach (var obj in objects)
                {
                    var response = await FormatExpressionAsync(request, obj.Expression!, language, cancellationToken);
                    if (!response.Success)
                    {
                        failedCount++;
                        results.Add(ToModelResult(obj, "failed"));
                        continue;
                    }

                    var status = Status(obj.Expression!, response.Formatted);
                    if (status == "formatted")
                        formattedCount++;
                    else
                        unchangedCount++;

                    successful.Add((obj, response.Formatted));
                    results.Add(ToModelResult(obj, status));
                }

                if (failedCount > 0)
                    return (false, "",
                        _ => (FormatModelResult)new ModelFormatResult(
                            objects.Count, formattedCount, unchangedCount, failedCount, results, null, null));

                foreach (var (obj, value) in successful)
                {
                    mutator.SetProperty(new ModelObjectSetRequest(
                        obj.Path,
                        [new ModelPropertyAssignment("expression", value)],
                        obj.Kind));
                }

                return (formattedCount > 0, $"format {formattedCount} expressions",
                    outcome => (FormatModelResult)new ModelFormatResult(
                        objects.Count, formattedCount, unchangedCount, failedCount,
                        results, outcome.Saved, outcome.Staged,
                        outcome.Synced, outcome.SyncTarget, outcome.SyncWarning));
            },
            (FormatModelResult)new ModelFormatResult(0, 0, 0, 0, [], null),
            cancellationToken);
    }

    private async Task<TomixResult<FormatModelResult>> FormatInlineAsync(
        FormatModelRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryResolveLanguage(request.Language, null, out var language, out var error))
            return TomixResult<FormatModelResult>.Fail("TOMIX_FORMAT_UNSUPPORTED_LANGUAGE", error, exitCode: 2);

        var formatted = await _formatter.FormatAsync(
            new ExpressionFormatRequest(
                request.Expression!,
                language,
                request.Long,
                request.Semicolons,
                request.NoSpaceAfterFunction),
            cancellationToken);

        return TomixResult<FormatModelResult>.Ok(
            new InlineFormatResult(formatted.Success, formatted.Formatted, formatted.Errors),
            exitCode: 0);
    }

    private Task<ExpressionFormatResponse> FormatExpressionAsync(
        FormatModelRequest request,
        string expression,
        string language,
        CancellationToken cancellationToken)
        => _formatter.FormatAsync(
            new ExpressionFormatRequest(
                expression,
                language,
                request.Long,
                request.Semicolons,
                request.NoSpaceAfterFunction),
            cancellationToken);

    private static IEnumerable<ModelObject> FormatTargets(
        ModelSnapshot snapshot,
        string language,
        ModelObjectKind? type)
    {
        var objects = ModelObjectProjection
            .Flatten(snapshot)
            .Where(o => !string.IsNullOrWhiteSpace(o.Expression));

        if (type is not null)
            objects = objects.Where(o => o.Kind == type.Value);
        else
            objects = language == FormatterLanguages.PowerQuery
                ? objects.Where(o => o.Kind == ModelObjectKind.Partition)
                : objects.Where(o => o.Kind == ModelObjectKind.Measure);

        return objects;
    }

    private static bool TryResolveLanguage(
        string? requested,
        ModelObjectKind? type,
        out string language,
        out string error)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var valid = FormatterLanguages.TryNormalize(requested, out language);
            error = valid ? "" : $"Unsupported formatter language: {requested}";
            return valid;
        }

        language = type == ModelObjectKind.Partition
            ? FormatterLanguages.PowerQuery
            : FormatterLanguages.Dax;
        error = "";
        return true;
    }

    private static string Status(string before, string after)
        => string.Equals(before, after, StringComparison.Ordinal) ? "unchanged" : "formatted";

    private static ModelFormatObjectResult ToModelResult(ModelObject obj, string status)
    {
        var table = obj.Path.Split('/')[0];
        return obj.Kind == ModelObjectKind.Partition
            ? new ModelFormatObjectResult(
                Measure: null,
                Table: table,
                Status: status,
                Partition: obj.Name)
            : new ModelFormatObjectResult(
                Measure: obj.Name,
                Table: table,
                Status: status,
                Partition: null);
    }
}
