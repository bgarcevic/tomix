namespace Tomix.App.Mutations;

/// <summary>
/// Detects a requested save. Persistence itself goes through <see cref="MutationLifecycle"/>
/// so every mutation receives the same validation gate.
/// </summary>
internal static class MutationSave
{
    /// <summary>True when the caller asked to persist (either <c>--save</c> or a <c>--save-to</c> path).</summary>
    public static bool Requested(bool save, string? saveTo)
        => save || !string.IsNullOrWhiteSpace(saveTo);

}
