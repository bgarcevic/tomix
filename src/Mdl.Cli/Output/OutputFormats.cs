using System.CommandLine;

namespace Mdl.Cli.Output;

/// <summary>
/// The shared <c>--format</c> option and its allowed values. Every command builds its format
/// option here so the alias, default, and accepted values stay identical across the CLI.
/// </summary>
internal static class OutputFormats
{
    public const string Human = "human";
    public const string Json = "json";

    public static Option<string> CreateOption()
    {
        var option = new Option<string>("--format")
        {
            Description = "Output format: human or json.",
            DefaultValueFactory = _ => Human
        };

        option.Aliases.Add("-f");
        return option;
    }

    public static bool IsValid(string format) => format is Human or Json;
}
