namespace Tomix.App.Bpa;

/// <summary>The outcome status of evaluating a rule expression against one scope.</summary>
public enum BpaEvaluationStatus
{
    Ok,
    CompilationError,
    EvaluationError
}

/// <summary>
/// Result of <see cref="BpaExpressionEvaluator.Evaluate{T}"/>: the matched elements on success, or
/// a non-Ok status with the diagnostic message when the expression could not be compiled or threw.
/// </summary>
public readonly record struct BpaEvaluation<T>(
    BpaEvaluationStatus Status,
    IReadOnlyList<T> Matches,
    string? ErrorMessage)
    where T : class
{
    public static BpaEvaluation<T> Ok(IReadOnlyList<T> matches)
        => new(BpaEvaluationStatus.Ok, matches, null);

    public static BpaEvaluation<T> Failed(BpaEvaluationStatus status, string message)
        => new(status, [], message);
}
