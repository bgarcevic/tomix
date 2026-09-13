using System.CommandLine;

namespace Tomix.Cli.Commands;

/// <summary>
/// Factory for the mutation lifecycle options (--save, --save-to, --serialization, --stage,
/// --revert, --no-sync) shared by every mutating command. Descriptions live here so the wording
/// cannot drift between commands; pass explicit text only for a genuinely command-specific
/// nuance. Fresh instances per call — options must not be shared between commands.
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
            Description = description ?? "Write the model even though validation reports errors"
        };
        option.Aliases.Add("-f");
        return option;
    }

    /// <summary>Lets the save output (--save-to, or the save/export target) replace an existing file.</summary>
    public static Option<bool> Overwrite(string? description = null) => new("--overwrite")
    {
        Description = description ?? "Allow --save-to to overwrite an existing target"
    };

    /// <summary>
    /// The uniform mutation preview: the change is applied to the in-memory model and its result
    /// is rendered, but nothing is written — no save, no stage, no workspace sync.
    /// </summary>
    public static Option<bool> DryRun() => new("--dry-run")
    {
        Description = "Preview: show the change without saving, staging, or syncing"
    };

    public static Option<bool> Save(string? description = null) => new("--save")
    {
        Description = description ??
            "Write this command's change back to the model's source. Cannot be combined with --revert or --stage."
    };

    public static Option<string?> SaveTo(string? description = null) => new("--save-to")
    {
        Description = description ?? "Write the saved model to this path instead of its source (implies --save)"
    };

    public static Option<string?> Serialization()
    {
        var option = new Option<string?>("--serialization")
        {
            Description = "How the model is written: tmdl or bim (tmsl and auto also accepted)"
        };
        option.AcceptAmongIgnoreCase("tmdl", "bim", "tmsl", "auto");
        return option;
    }

    public static Option<bool> Stage() => new("--stage")
    {
        Description = "Stage this command's mutation"
    };

    public static Option<bool> Revert() => new("--revert")
    {
        Description = "Revert a staged mutation"
    };

    public static Option<bool> NoSync() => new("--no-sync")
    {
        Description = "Skip workspace sync when workspace mode is active."
    };
}
