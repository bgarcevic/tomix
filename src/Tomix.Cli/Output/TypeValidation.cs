using Spectre.Console;

namespace Tomix.Cli.Output;

internal static class TypeValidation
{
    private static readonly string ValidTypes = "table, measure, column, calculatedcolumn, hierarchy, partition, relationship, role, perspective, culture";

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
