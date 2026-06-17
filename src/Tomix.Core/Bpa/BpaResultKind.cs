namespace Tomix.Core.Bpa;

/// <summary>
/// The distinct outcomes the analyzer can produce for a rule. A rule may yield zero or more
/// <see cref="Violation"/> results, or exactly one sentinel result for the non-violation states.
/// </summary>
public enum BpaResultKind
{
    /// <summary>A rule predicate matched a model object.</summary>
    Violation,

    /// <summary>The rule is disabled globally (model-level ignore annotation); it was not evaluated.</summary>
    DisabledRule,

    /// <summary>The model's compatibility level is below the rule's minimum; it was not evaluated.</summary>
    InvalidCompatibilityLevel,

    /// <summary>The rule expression could not be compiled for a scope.</summary>
    CompilationError,

    /// <summary>The rule expression compiled but threw while evaluating a scope.</summary>
    EvaluationError
}
