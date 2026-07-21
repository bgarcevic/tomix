using System.CommandLine;
using Tomix.Cli.Output;

namespace Tomix.Cli.Commands;

internal static class GlobalOptions
{
    public static readonly Option<string?> Model = new("--model")
    {
        Description = "Path to semantic model (TMDL folder, .bim file, or TE folder)",
        Recursive = true
    };

    public static readonly Option<string> OutputFormat = OutputFormats.CreateOption();

    public static readonly Option<string?> ErrorFormat = new("--error-format")
    {
        Description = "Stderr format for errors/warnings/hints: text (default) or json. Other values fall back to text.",
        Recursive = true
    };

    public static readonly Option<string?> Server = new("--server")
    {
        Description = "Workspace name or endpoint (e.g., MyWorkspace, powerbi://..., asazure://..., localhost)",
        Recursive = true
    };

    public static readonly Option<string?> Database = new("--database")
    {
        Description = "Semantic model name on the workspace",
        Recursive = true
    };

    public static readonly Option<string?> Auth = new("--auth")
    {
        Description = "Auth method: auto, interactive, spn, managed-identity (default: auto)",
        Recursive = true
    };

    public static readonly Option<string?> Recent = new("--recent")
    {
        Description = "Use a recently used model. No value = interactive picker, N = Nth most recent (1 = last used).",
        Arity = ArgumentArity.ZeroOrOne,
        Recursive = true
    };

    public static readonly Option<bool> Debug = new("--debug")
    {
        Description = "Show the full stack trace on stderr when an unexpected error occurs",
        Recursive = true
    };

    public static readonly Option<bool> NonInteractive = new("--non-interactive")
    {
        Description = "Disable all interactive prompts. Fail with an actionable error if required input is missing.",
        Recursive = true
    };

    public static readonly Option<bool> Yes = new("--yes")
    {
        Description = "Skip confirmation prompts for destructive operations",
        Recursive = true
    };

    public static readonly Option<bool> Quiet = new("--quiet")
    {
        Description = "Suppress non-essential output (spinners, progress, hints). Errors and data still print.",
        Recursive = true
    };

    static GlobalOptions()
    {
        Model.Aliases.Add("-m");
        Recent.Aliases.Add("--recents");
        Server.Aliases.Add("-s");
        Database.Aliases.Add("-d");
        Yes.Aliases.Add("-y");
        // No -q alias: several commands (add, set, get, bpa) use a local -q for
        // property/query input, so -q-as-quiet would be silently shadowed there.
        OutputFormat.Recursive = true;
    }

    public static IEnumerable<Option> All()
    {
        yield return Model;
        yield return OutputFormat;
        yield return ErrorFormat;
        yield return Server;
        yield return Database;
        yield return Auth;
        yield return Recent;
        yield return Debug;
        yield return NonInteractive;
        yield return Yes;
        yield return Quiet;
    }

    public static string OutputFormatValue(ParseResult parseResult)
        => parseResult.GetValue(OutputFormat) ?? OutputFormats.Text;

    public static string? ModelValue(ParseResult parseResult)
        => parseResult.GetValue(Model);

    public static string? AuthValue(ParseResult parseResult)
        => parseResult.GetValue(Auth);

    /// <summary>True when --recent was passed, with or without a value (arity is ZeroOrOne).</summary>
    public static bool RecentSpecified(ParseResult parseResult)
        => parseResult.GetResult(Recent) is not null;

    public static string? RecentValue(ParseResult parseResult)
        => parseResult.GetValue(Recent);
}
