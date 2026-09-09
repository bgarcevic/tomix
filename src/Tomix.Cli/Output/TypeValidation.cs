using Tomix.Core.Diagnostics;
using Tomix.Core.Models;

namespace Tomix.Cli.Output;

internal static class TypeValidation
{
    /// <summary>Derived from the shared vocabulary so it cannot drift from the parser.</summary>
    private static readonly string ValidTypes = ModelObjectTypeCatalog.DiscoveryListText;

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
