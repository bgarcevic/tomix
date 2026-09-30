namespace Tomix.Core.Bpa;

/// <summary>
/// Which level switched a whole rule off. A rule can be off at both levels at once, and turning
/// one back on leaves the other in force.
/// </summary>
[Flags]
public enum BpaRuleSuppression
{
    None = 0,

    /// <summary>Disabled for the current user (<c>bpa rules ignore --user</c>), on this machine only.</summary>
    User = 1,

    /// <summary>Ignored by the model's <c>BestPracticeAnalyzer_IgnoreRules</c> annotation (<c>bpa rules ignore</c>).</summary>
    Model = 2
}
