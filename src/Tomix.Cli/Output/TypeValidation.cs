using Spectre.Console;

namespace Tomix.Cli.Output;

internal static class TypeValidation
{
    private static readonly string ValidTypes = "table, measure, column, calculatedcolumn, hierarchy, level, partition, calculationitem, relationship, role, member, perspective, culture, datasource, kpi, tablepermission, calendar";

    public static int WriteInvalidTypeError()
    {
        var err = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(Console.Error)
        });
        err.MarkupLine(Styling.Error("Invalid --type value."));
        err.MarkupLine(Styling.Guidance($"  → Valid types: {ValidTypes}"));
        return 2;
    }
}
