using Spectre.Console;
using Tomix.App.Diff;

namespace Tomix.Cli.Output;

/// <summary>One change of a model diff, as <c>tx diff</c> and the <c>tx deploy</c> preview print it.</summary>
internal static class DiffChangeRenderer
{
    public static void Render(DiffChange change)
    {
        switch (change.Action)
        {
            case "added":
                AnsiConsole.MarkupLine($"  {Styling.Success("+")} {Styling.MarkupEscape(change.ObjectType)} {Styling.Path(change.Path)}");
                break;
            case "removed":
                AnsiConsole.MarkupLine($"  {Styling.Error("-")} {Styling.MarkupEscape(change.ObjectType)} {Styling.Path(change.Path)}");
                break;
            case "modified":
                // A modified change carries "<Kind>/<object path>" and the changed property's name.
                var separator = change.ObjectType.IndexOf('/');
                var kind = separator < 0 ? change.ObjectType : change.ObjectType[..separator];
                var path = separator < 0 ? "" : change.ObjectType[(separator + 1)..];
                AnsiConsole.MarkupLine($"  {Styling.Warning("~")} {Styling.MarkupEscape(kind)} {Styling.Path(path)}: {Styling.MarkupEscape(PropertyLabel(kind, change.Path))}");
                AnsiConsole.MarkupLine($"    {Styling.Error($"- {change.OldValue}")}");
                AnsiConsole.MarkupLine($"    {Styling.Success($"+ {change.NewValue}")}");
                break;
        }
    }

    /// <summary>
    /// A column's Detail holds its data type, so text output names it that; JSON keeps
    /// <c>Detail</c>, which is part of its contract.
    /// </summary>
    private static string PropertyLabel(string kind, string property)
        => property == "Detail" && kind is "Column" or "CalculatedColumn" ? "DataType" : property;
}
