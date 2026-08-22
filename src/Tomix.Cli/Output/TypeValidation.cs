using Tomix.Core.Diagnostics;

namespace Tomix.Cli.Output;

internal static class TypeValidation
{
    private static readonly string ValidTypes = "table, measure, column, calculatedcolumn, hierarchy, level, partition, calculationitem, relationship, role, member, perspective, culture, datasource, kpi, tablepermission, calendar, expression, function";

    /// <summary>
    /// Reports an unrecognized <c>--type</c> value and returns exit code 2. Routed through
    /// <see cref="ErrorOutput"/> rather than writing markup straight to stderr so the failure
    /// carries a documented code and honors <c>--error-format json</c> like every other
    /// usage error.
    /// </summary>
    public static int WriteInvalidTypeError(string? errorFormat)
    {
        ErrorOutput.Write(
            [new TomixDiagnostic(
                "TOMIX_INVALID_TYPE",
                DiagnosticSeverity.Error,
                "Invalid --type value.",
                $"Valid types: {ValidTypes}")],
            errorFormat);
        return 2;
    }
}
