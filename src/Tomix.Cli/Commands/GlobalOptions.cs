using System.CommandLine;
using System.CommandLine.Parsing;
using Tomix.Cli.Output;

namespace Tomix.Cli.Commands;

internal static class GlobalOptions
{
    private static string _defaultOutputFormat = OutputFormats.Text;
    public static readonly Option<string?> Model = new("--model")
    {
        Description = "Path to semantic model (TMDL folder, .bim file, or TE folder)",
        Recursive = true
    };

    public static readonly Option<string> OutputFormat = new("--output-format")
    {
        Description = "Stdout format: text (default), json, csv, tmsl (alias: bim), tmdl. Not all formats are supported by every command.",
        DefaultValueFactory = _ => _defaultOutputFormat
    };

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

    public static string OutputFormatValue(ParseResult parseResult, Option<string> localOption)
    {
        if (parseResult.GetResult(localOption) is { Implicit: false })
            return parseResult.GetValue(localOption) ?? OutputFormats.Text;

        if (parseResult.GetResult(OutputFormat) is { Implicit: false })
            return parseResult.GetValue(OutputFormat) ?? OutputFormats.Text;

        return parseResult.GetValue(localOption) ?? OutputFormats.Text;
    }

    /// <summary>
    /// The effective stderr format for a command's diagnostics: an explicit <c>--error-format</c>
    /// wins, otherwise JSON stdout implies JSON errors. Without the second half, a script that asks
    /// for <c>--output-format json</c> still has to parse a colored text error off stderr — which is
    /// what docs/error-codes.md has always promised it would not have to do. Every command resolves
    /// its error format through here so the rule holds uniformly.
    /// </summary>
    public static string? ErrorFormatValue(ParseResult parseResult, string outputFormat)
        => parseResult.GetValue(ErrorFormat)
           ?? (OutputFormats.IsJson(outputFormat) ? OutputFormats.Json : null);

    /// <summary>
    /// <see cref="ErrorFormatValue(ParseResult, string)"/> for paths that run before a command has
    /// resolved its own output format — the top-level crash handler, the unknown-option guard, and
    /// shared helpers like <see cref="RecentConnections"/> that fail before the command body runs.
    /// </summary>
    public static string? ErrorFormatValue(ParseResult parseResult)
        => ErrorFormatValue(parseResult, EffectiveOutputFormat(parseResult));

    /// <summary>
    /// The <c>--output-format</c> value as actually parsed, whichever option carried it.
    /// </summary>
    /// <remarks>
    /// <c>doctor</c>, <c>update</c> and <c>completion</c> declare their own local
    /// <c>--output-format</c> alongside the recursive global one — which is why
    /// <see cref="OutputFormatValue(ParseResult, Option{string})"/> exists at all. Reading
    /// <see cref="OutputFormat"/> alone reports its *default* for exactly those commands, so a
    /// caller that passed <c>--output-format json</c> to one of them would still get a text error
    /// off stderr. Walking outward from the parsed command finds whichever option was bound,
    /// innermost first, matching that helper's local-then-global precedence.
    /// </remarks>
    private static string EffectiveOutputFormat(ParseResult parseResult)
    {
        for (SymbolResult? symbol = parseResult.CommandResult; symbol is not null; symbol = symbol.Parent)
        {
            if (symbol is not CommandResult command)
                continue;

            foreach (var option in command.Command.Options)
            {
                if (option is not Option<string> formatOption
                    || !string.Equals(formatOption.Name, OutputFormat.Name, StringComparison.Ordinal))
                    continue;

                if (parseResult.GetResult(formatOption) is { Implicit: false })
                    return parseResult.GetValue(formatOption) ?? OutputFormats.Text;
            }
        }

        return OutputFormatValue(parseResult);
    }

    public static void ConfigureDefaultOutputFormat(string? format)
        => _defaultOutputFormat = string.Equals(format, OutputFormats.Json, StringComparison.OrdinalIgnoreCase)
            ? OutputFormats.Json
            : OutputFormats.Text;

    public static string DefaultOutputFormat => _defaultOutputFormat;

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
