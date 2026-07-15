namespace Tomix.Core.Vertipaq;

/// <summary>
/// Wraps failures from the statistics extraction/import layer so handlers never catch
/// provider-library exceptions directly. <see cref="Kind"/> tells the handler which
/// diagnostic code to surface.
/// </summary>
public sealed class VertipaqAnalysisException : Exception
{
    public VertipaqAnalysisKind Kind { get; }

    public VertipaqAnalysisException(VertipaqAnalysisKind kind, string message, Exception? inner = null)
        : base(message, inner)
        => Kind = kind;
}

public enum VertipaqAnalysisKind
{
    /// <summary>Statistics extraction against the live engine failed.</summary>
    ExtractionFailed,

    /// <summary>The <c>.vpax</c> file is missing, unreadable, or not a valid stats package.</summary>
    VpaxReadFailed,

    /// <summary>The <c>.vpax</c> (or dictionary) target could not be written.</summary>
    VpaxWriteFailed
}
