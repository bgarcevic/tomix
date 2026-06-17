using System.CommandLine;

namespace Tomix.Cli.Output;

/// <summary>
/// The shared <c>--format</c> option and its allowed values. Every command builds its format
/// option here so the alias, default, and accepted values stay identical across the CLI.
/// </summary>
internal static class OutputFormats
{
    public const string Auto = "auto";
    public const string Text = "text";
    public const string Json = "json";
    public const string Csv = "csv";
    public const string Tmsl = "tmsl";
    public const string Bim = "bim";
    public const string Tmdl = "tmdl";

    public static Option<string> CreateOption()
    {
        var option = new Option<string>("--output-format")
        {
            Description = "Stdout format: text (default), json, csv, tmsl (alias: bim), tTomix. Not all formats are supported by every command.",
            DefaultValueFactory = _ => Text
        };

        return option;
    }

    public static bool IsValid(string format)
        => format is Auto or Text or Json or Csv or Tmsl or Bim or Tmdl;

    public static bool IsJson(string format) => format == Json;

    public static bool IsCsv(string format) => format == Csv;

    public static bool IsTextLike(string format) => format is Text or Auto;
}
