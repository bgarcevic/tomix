namespace Tomix.Core.Dax;

/// <summary>What a run of DAX source text is, for syntax highlighting.</summary>
public enum DaxTextClassification
{
    Text,
    Keyword,
    Function,
    StringLiteral,
    Number,
    Comment,
    TableName,
    ColumnReference,

    /// <summary>A bracketed reference that resolves to a measure in the model, not a column.</summary>
    MeasureReference,
    Variable,
    QueryParameter,
    Parenthesis,
    DefinitionName,
    Operator,
    Punctuation,
}

/// <summary>A run of DAX source text and its classification. Spans are ordered and never overlap.</summary>
public readonly record struct DaxClassifiedSpan(
    int Start,
    int Length,
    DaxTextClassification Classification);

/// <summary>
/// The public surface of the DAX language engine: syntax-aware classification of DAX source for
/// highlighting. The classification comes from a full tokenize-and-parse pass, which is why a
/// string containing "--", a multi-line comment, or a variable named after a function are all
/// classified correctly.
/// </summary>
public static class DaxLanguage
{
    public static IReadOnlyList<DaxClassifiedSpan> Classify(string dax)
        => Engine.DaxClassifier.Classify(dax)
            .Select(span => new DaxClassifiedSpan(
                span.Start,
                span.Length,
                Map(span.Kind)))
            .ToArray();

    /// <summary>
    /// Classifies <paramref name="dax"/> and resolves bracketed references against the model's
    /// measure names: a <c>[Name]</c> — qualified or not — whose name is in
    /// <paramref name="measureNames"/> comes back as <see cref="DaxTextClassification.MeasureReference"/>
    /// instead of a column. The syntax classification alone cannot tell measures from columns,
    /// because both are bracketed names and the engine deliberately sees no model; the caller
    /// supplies that knowledge. The set should be case-insensitive, like DAX name resolution,
    /// and a name that exists as both wins as a measure.
    /// </summary>
    public static IReadOnlyList<DaxClassifiedSpan> Classify(string dax, IReadOnlySet<string>? measureNames)
    {
        var spans = Classify(dax);
        if (measureNames is null || measureNames.Count == 0)
            return spans;

        return spans
            .Select(span => span.Classification == DaxTextClassification.ColumnReference
                && MeasureNameOf(dax, span) is { } name
                && measureNames.Contains(name)
                    ? span with { Classification = DaxTextClassification.MeasureReference }
                    : span)
            .ToArray();
    }

    /// <summary>The bracketed span's name with DAX's doubled-delimiter escapes undone, or null.</summary>
    private static string? MeasureNameOf(string dax, DaxClassifiedSpan span)
    {
        if (span.Length < 2 || dax[span.Start] != '[' || dax[span.Start + span.Length - 1] != ']')
            return null;

        return dax.Substring(span.Start + 1, span.Length - 2).Replace("]]", "]", StringComparison.Ordinal);
    }

    private static DaxTextClassification Map(Engine.DaxClassification classification) =>
        classification switch
        {
            Engine.DaxClassification.Keyword => DaxTextClassification.Keyword,
            Engine.DaxClassification.Function => DaxTextClassification.Function,
            Engine.DaxClassification.StringLiteral => DaxTextClassification.StringLiteral,
            Engine.DaxClassification.Number => DaxTextClassification.Number,
            Engine.DaxClassification.Comment => DaxTextClassification.Comment,
            Engine.DaxClassification.TableName => DaxTextClassification.TableName,
            Engine.DaxClassification.ColumnReference => DaxTextClassification.ColumnReference,
            Engine.DaxClassification.Variable => DaxTextClassification.Variable,
            Engine.DaxClassification.QueryParameter => DaxTextClassification.QueryParameter,
            Engine.DaxClassification.Parenthesis => DaxTextClassification.Parenthesis,
            Engine.DaxClassification.DefinitionName => DaxTextClassification.DefinitionName,
            Engine.DaxClassification.Operator => DaxTextClassification.Operator,
            Engine.DaxClassification.Punctuation => DaxTextClassification.Punctuation,
            _ => DaxTextClassification.Text,
        };
}
