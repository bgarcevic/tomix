using System.CommandLine;

namespace Tomix.Cli.Commands;

internal static class OptionValidators
{
    /// <summary>
    /// Rejects the option at parse time unless the value matches one of <paramref name="allowed"/>
    /// (case-insensitive). Unlike <c>AcceptOnlyFromAmong</c>, casing is not significant, matching
    /// the case-insensitive parsing used at apply time.
    /// </summary>
    public static void AcceptAmongIgnoreCase(this Option<string?> option, params string[] allowed)
        => option.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<string?>();
            if (!string.IsNullOrWhiteSpace(value) &&
                !allowed.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                result.AddError(
                    $"Unknown value for {option.Name}: '{value}'. Known values: {string.Join(", ", allowed)}.");
            }
        });
}
