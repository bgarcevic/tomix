using System.Globalization;
using Spectre.Console;
using Tomix.App.Dax;
using Tomix.App.Get;
using Tomix.App.M;
using Tomix.Core.Models;
using Tomix.Core.Properties;

namespace Tomix.Cli.Output;

internal static class GetRenderer
{
    public static void Render(GetObjectResult result, string format, bool all = false)
    {
        if (IsScalarQuery(result))
        {
            RenderScalar(result.Properties.Values.First());
            return;
        }

        // TMDL, BIM and TMSL come from the provider's serializer (GetModelRequest.Script).
        if (result.Script is { } script)
        {
            Console.WriteLine(script.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n'));
            return;
        }

        RenderProperties(result, all);
    }

    public static void RenderCsv(GetObjectResult result)
    {
        if (result.Properties.Count == 1)
        {
            CsvOutput.WriteValue(result.Properties.Values.First());
            return;
        }

        PropertyCsvRenderer.Write(ModelPropertyCatalog.For(result.Object.Kind), result.Properties);
    }

    public static object? ToReferenceJson(GetObjectResult result)
        => IsScalarQuery(result) ? result.Properties.Values.First() : result;

    private static bool IsScalarQuery(GetObjectResult result)
        => result.Properties.Count == 1;

    private const string Indent = "  ";

    private static void RenderProperties(GetObjectResult result, bool all)
    {
        // A terminal wraps at its width; redirected output stays unwrapped so a value (or an
        // expression line) is never split across lines for grep or a file.
        if (!Console.IsOutputRedirected)
        {
            RenderPropertiesCore(result, all);
            return;
        }

        var width = AnsiConsole.Profile.Width;
        AnsiConsole.Profile.Width = int.MaxValue;
        try
        {
            RenderPropertiesCore(result, all);
        }
        finally
        {
            AnsiConsole.Profile.Width = width;
        }
    }

    private static void RenderPropertiesCore(GetObjectResult result, bool all)
    {
        var view = GetView.Build(result, all);

        // Under --all the header counts what was authored, since defaults are listed too.
        var summary = all ? $"  · {view.Properties.Count(row => !row.IsDefault)} of {view.Properties.Count} set" : "";
        AnsiConsole.MarkupLine($"{Styling.Title(view.Title)}  {Styling.Muted(view.Kind + summary)}");

        RenderSection(null, view.Properties, result);
        RenderSection("Annotations", view.Annotations, result);
        RenderSection("Translations", view.Translations, result);

        if (view.Unset.Count > 0)
        {
            Console.WriteLine();
            AnsiConsole.MarkupLine(Wrapped($"{Indent}Not set: ", view.Unset));
        }
    }

    /// <summary>
    /// The stderr footer after the text view: how to set one of the folded properties and how
    /// to list them all. Commentary, so it never reaches a redirected stdout.
    /// </summary>
    public static void RenderHint(GetObjectResult result, bool all)
    {
        if (all || IsScalarQuery(result) || GetView.Build(result, all).Unset.Count == 0)
            return;

        var path = result.Object.Kind == ModelObjectKind.Model ? "." : result.Path;
        var quoted = path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;
        StdErr.MarkupLine(Styling.Guidance($"  → Set one with 'tx set {quoted} --set <property>=<value>'; --all lists every property."));
    }

    private static void RenderSection(string? heading, IReadOnlyList<GetViewRow> rows, GetObjectResult result)
    {
        if (rows.Count == 0)
            return;

        Console.WriteLine();
        var indent = Indent;
        if (heading is not null)
        {
            AnsiConsole.MarkupLine($"{Indent}{Styling.Bold(heading)}");
            indent += Indent;
        }

        var keyWidth = rows.Max(row => row.Key.Length);
        foreach (var row in rows)
        {
            var value = ValueMarkup(row, result);
            var tag = row.ReadOnly ? "  " + Styling.Muted("read-only") : "";
            if (!value.Contains('\n', StringComparison.Ordinal))
            {
                // A one-row grid per line: a long value wraps inside its own column instead of
                // back to the margin, and every row shares the section's key width.
                var grid = new Grid()
                    .AddColumn(new GridColumn { Width = keyWidth, NoWrap = true, Padding = new Padding(indent.Length, 0, 2, 0) })
                    .AddColumn(new GridColumn { Padding = new Padding(0) });
                grid.AddRow(new Markup(Styling.Muted(row.Key)), new Markup(value + tag));
                AnsiConsole.Write(grid);
                continue;
            }

            // Multi-line values (formatted DAX, M queries, policy issues) go under their key as an
            // indented block, so the text stays copyable and keeps its own indentation.
            var blockIndent = indent + Indent;
            AnsiConsole.MarkupLine($"{indent}{Styling.Muted(row.Key)}{tag}");
            AnsiConsole.MarkupLine(blockIndent + value.Replace("\n", "\n" + blockIndent, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A row's value as markup. Defaults (listed only under --all) render muted; DAX and M values
    /// render syntax-highlighted; everything else stays plain. Expressions are matched by value,
    /// not by property key: the display keys are the camelCase catalog keys while DaxExpressions
    /// reports the snapshot contract's PascalCase keys.
    /// </summary>
    private static string ValueMarkup(GetViewRow row, GetObjectResult result)
    {
        if (row.IsDefault)
        {
            var text = ScalarText(row.Value);
            return Styling.Muted(text.Length == 0 ? "—" : text);
        }

        return row.Value switch
        {
            string text when DaxExpressions.IsDaxValue(result.Object, text)
                => Styling.ExpressionMarkup(ExpressionLanguage.Dax, Normalize(text), result.MeasureNames),
            string m when MExpressions.IsMValue(result.Object, m)
                => Styling.ExpressionMarkup(ExpressionLanguage.M, Normalize(m)),
            IReadOnlyList<RefreshPolicyIssue> issues
                => string.Join('\n', issues.Select(i => Styling.MarkupEscape($"{i.Severity} [{i.Code}]: {i.Message}"))),
            _ => Styling.MarkupEscape(Normalize(ScalarText(row.Value)))
        };
    }

    private static string Normalize(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');

    /// <summary>Joins <paramref name="items"/> after a label, wrapping at the console width with a hanging indent.</summary>
    private static string Wrapped(string label, IReadOnlyList<string> items)
    {
        var width = Math.Max(40, AnsiConsole.Profile.Width);
        var hanging = new string(' ', label.Length);
        var lines = new List<string>();
        var line = label;
        for (var i = 0; i < items.Count; i++)
        {
            var item = i < items.Count - 1 ? items[i] + "," : items[i];
            if (line.Length > label.Length && line.Length + 1 + item.Length > width)
            {
                lines.Add(line);
                line = hanging + item;
                continue;
            }

            line += line.Length > label.Length ? " " + item : item;
        }

        lines.Add(line);
        return Styling.Muted(string.Join('\n', lines));
    }

    private static void RenderScalar(object? value)
    {
        Console.WriteLine(value is IReadOnlyList<RefreshPolicyIssue> issues
            ? string.Join(Environment.NewLine, issues.Select(i => $"{i.Severity} [{i.Code}]: {i.Message}"))
            : ScalarText(value));
    }

    private static string ScalarText(object? value) => value switch
    {
        null => "",
        bool b => b ? "True" : "False",
        IReadOnlyList<string> names => string.Join(", ", names),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };
}
