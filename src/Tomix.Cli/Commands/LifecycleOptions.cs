using System.CommandLine;
using Tomix.Cli.Output;

namespace Tomix.Cli.Commands;

/// <summary>
/// Factory for the mutation lifecycle options (--save, --save-to, --serialization, --stage,
/// --revert, --no-sync) shared by every mutating command. Descriptions live here so the wording
/// cannot drift between commands; pass explicit text only for a genuinely command-specific
/// nuance. Fresh instances per call — options must not be shared between commands. Every option
/// is tagged <see cref="HelpGroups.Save"/>, so help lists them together after the command's own.
/// </summary>
internal static class LifecycleOptions
{
    /// <summary>
    /// Bypass validation that would otherwise block the save (DAX/refresh-policy errors).
    /// Deliberately narrow: overwriting an existing target needs <see cref="Overwrite"/>,
    /// and destructive-guard bypasses (rm dependents, init/connect overwrite, deploy checks)
    /// keep their own command-scoped --force with an explicit description.
    /// </summary>
    public static Option<bool> Force(string? description = null)
    {
        var option = new Option<bool>("--force")
        {
            Description = description ?? "Save even if the change adds validation errors"
        };
        option.Aliases.Add("-f");
        return option.In(HelpGroups.Save);
    }

    /// <summary>Lets the save output (--save-to, or the save/export target) replace an existing file.</summary>
    public static Option<bool> Overwrite(string? description = null) => new Option<bool>("--overwrite")
    {
        Description = description ?? "Allow --save-to to overwrite an existing target"
    }.In(HelpGroups.Save);

    /// <summary>
    /// True when the mutation will be written (--save, --save-to, or --stage). Without any of them
    /// a mutation is a preview, so it never needs a confirmation prompt.
    /// </summary>
    public static bool Persists(bool save, string? saveTo, bool stage)
        => save || stage || !string.IsNullOrWhiteSpace(saveTo);

    public static Option<bool> Save(string? description = null) => new Option<bool>("--save")
    {
        Description = description ??
            "Write the change to the model's source (default: preview only, nothing is written)"
    }.In(HelpGroups.Save);

    public static Option<string?> SaveTo(string? description = null) => new Option<string?>("--save-to")
    {
        Description = description ?? "Write the saved model to this path instead of its source (implies --save)"
    }.In(HelpGroups.Save);

    public static Option<string?> Serialization()
    {
        var option = new Option<string?>("--serialization")
        {
            Description = "How the model is written: tmdl or bim (tmsl and auto also accepted)"
        };
        option.AcceptAmongIgnoreCase("tmdl", "bim", "tmsl", "auto");
        return option.In(HelpGroups.Save);
    }

    public static Option<bool> Stage() => new Option<bool>("--stage")
    {
        Description = "Stage the change instead of saving it (commit with 'tx stage commit')"
    }.In(HelpGroups.Save);

    public static Option<bool> Revert() => new Option<bool>("--revert")
    {
        Description = "Discard this model's staged changes"
    }.In(HelpGroups.Save);

    public static Option<bool> NoSync() => new Option<bool>("--no-sync")
    {
        Description = "Skip workspace sync when workspace mode is active"
    }.In(HelpGroups.Save);
}
