using Spectre.Console;
using Tomix.App.Vertipaq;

namespace Tomix.Cli.Output;

/// <summary>
/// Spectre rendering for <c>vertipaq</c>: summary key-values, one table per selected section
/// (numeric columns right-aligned, bar column colored), plus export/annotate confirmations.
/// Layout decisions live in <see cref="VertipaqView"/>; this file only formats and prints.
/// </summary>
internal static class VertipaqRenderer
{
    public static void Render(VertipaqResult data, VertipaqView.ViewOptions options)
    {
        if (data.UsedRemoteFallback)
            AnsiConsole.MarkupLine(
                Styling.Muted($"Statistics read from the connected remote model: {data.AnalyzedSource}"));

        if (options.Stats)
            RenderSummary(data);

        foreach (var section in VertipaqView.BuildSections(data.Stats, options))
            RenderSection(section);

        if (data.ExportedPath is not null)
        {
            AnsiConsole.MarkupLine(Styling.Success($"Exported {data.ExportedPath}"));
            if (data.ObfuscationDictionaryPath is not null)
                AnsiConsole.MarkupLine(Styling.Warning(
                    $"Obfuscation dictionary written to {data.ObfuscationDictionaryPath} — " +
                    "required to deobfuscate; keep it private and out of version control."));
        }

        if (data.Annotate is { } annotate)
            RenderAnnotate(annotate);
    }

    private static void RenderSummary(VertipaqResult data)
    {
        AnsiConsole.MarkupLine(Styling.Title("Storage summary"));
        foreach (var (label, value) in VertipaqView.BuildSummary(data.Stats))
            AnsiConsole.MarkupLine("  " + Styling.KeyValue(label + ":", value));
        AnsiConsole.WriteLine();
    }

    private static void RenderSection(VertipaqView.SectionTable section)
    {
        var shown = section.Rows.Count;
        var suffix = shown < section.TotalCount
            ? $"top {shown} of {section.TotalCount}"
            : shown.ToString();
        AnsiConsole.MarkupLine(Styling.Title($"{section.Title} ({suffix})"));

        if (shown == 0)
        {
            AnsiConsole.MarkupLine(Styling.Muted("No storage statistics matched."));
            AnsiConsole.WriteLine();
            return;
        }

        var table = Styling.NewTable(section.Fields.Select(f => f.Header).ToArray());
        for (var i = 0; i < section.Fields.Count; i++)
        {
            if (IsNumeric(section.Fields[i].Kind))
                table.Columns[i].Alignment = Justify.Right;
        }

        foreach (var row in section.Rows)
            table.AddRow(row.Select((cell, i) => Cell(section.Fields[i].Kind, cell)).ToArray());

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static void RenderAnnotate(VertipaqAnnotateResult annotate)
    {
        var skipped = annotate.SkippedObjects > 0
            ? $" ({annotate.SkippedObjects} not present in the model, skipped)"
            : "";

        if (annotate.Saved is false or null)
        {
            AnsiConsole.MarkupLine(Styling.Warning(
                $"Annotated {annotate.AnnotatedObjects} objects in memory{skipped} — pass --save to persist."));
            return;
        }

        AnsiConsole.MarkupLine(Styling.Success(
            $"Annotated {annotate.AnnotatedObjects} objects and saved{skipped}."));

        if (annotate.Synced && annotate.SyncTarget is not null)
            AnsiConsole.MarkupLine(Styling.Muted($"Synced workspace mirror: {annotate.SyncTarget}"));
        if (annotate.SyncWarning is not null)
            AnsiConsole.MarkupLine(Styling.Warning(annotate.SyncWarning));
    }

    private static bool IsNumeric(VertipaqView.FieldKind kind)
        => kind is VertipaqView.FieldKind.Integer or VertipaqView.FieldKind.Percent or VertipaqView.FieldKind.Ratio;

    private static string Cell(VertipaqView.FieldKind kind, object? value) => kind switch
    {
        VertipaqView.FieldKind.Integer => value is long l ? Styling.Number(l)
            : value is int i ? Styling.Number(i) : "",
        VertipaqView.FieldKind.Percent => value is double d ? VertipaqView.Percent(d) : "",
        VertipaqView.FieldKind.Ratio => VertipaqView.RatioText(value as double?),
        VertipaqView.FieldKind.Bool => value is true ? Styling.BoolText(true) : Styling.BoolText(false),
        VertipaqView.FieldKind.Bar => $"[{Palette.Harbor.ToMarkup()}]{value}[/]",
        _ => Styling.MarkupEscape(value?.ToString() ?? "")
    };
}
