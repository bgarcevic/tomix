namespace Tomix.Cli.Commands;

internal static class MutationSpinnerLabel
{
    /// <summary>Spinner label matching the mutation's resolved persistence mode.</summary>
    public static string For(bool save, string? saveTo, bool stage, bool revert)
        => revert ? "Reverting..."
            : stage ? "Staging..."
            : save || !string.IsNullOrWhiteSpace(saveTo) ? "Saving..."
            : "Working...";
}
