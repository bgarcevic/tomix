using Spectre.Console;
using Tomix.App.Deps;

namespace Tomix.Cli.Output;

internal static class DepsRenderer
{
    public static void Render(
        DepsModelResult result,
        bool showUpstream,
        bool showDownstream,
        bool deep,
        bool quiet)
    {
        if (result.Unused is not null)
        {
            RenderUnused(result.Unused);
            return;
        }

        if (!quiet)
        {
            AnsiConsole.MarkupLine(Styling.Value("Running semantic analysis..."));
            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine(Styling.Title($"Dependencies for {result.Path} ({result.Type})"));
        if (showUpstream)
        {
            AnsiConsole.WriteLine();
            RenderSection("Upstream (depends on)", result.Upstream, deep);
        }

        if (showDownstream)
        {
            AnsiConsole.WriteLine();
            RenderSection("Downstream (referenced by)", result.Downstream, deep);
        }
    }

    public static object ToReferenceJson(
        DepsModelResult result,
        bool includeUpstream,
        bool includeDownstream)
    {
        if (result.Unused is not null)
            return new Dictionary<string, object?>
            {
                ["unused"] = result.Unused.Select(ToReferenceJson).ToList()
            };

        var json = new Dictionary<string, object?>
        {
            ["path"] = result.Path,
            ["objectType"] = result.Type
        };

        if (includeUpstream)
            json["upstream"] = result.Upstream.Select(ToReferenceJson).ToList();
        if (includeDownstream)
            json["downstream"] = result.Downstream.Select(ToReferenceJson).ToList();

        return json;
    }

    private static void RenderSection(string title, IReadOnlyList<DependencyObject> dependencies, bool deep)
    {
        var header = $"{title}: {dependencies.Count}";
        if (dependencies.Count == 0)
        {
            AnsiConsole.MarkupLine(Styling.Bold(header));
            AnsiConsole.MarkupLine(Styling.Muted("None"));
            return;
        }

        if (deep)
        {
            var tree = new Tree(Styling.Bold(header));
            AddTreeNodes(tree, dependencies);
            AnsiConsole.Write(tree);
            return;
        }

        AnsiConsole.MarkupLine(Styling.Bold(header));
        var table = Styling.NewTable("Type", "Reference", "Path");
        foreach (var dependency in dependencies)
            table.AddRow(
                Styling.MarkupEscape(dependency.Type),
                Styling.MarkupEscape(dependency.Reference),
                Styling.MarkupEscape(dependency.Path));
        AnsiConsole.Write(table);
    }

    private static void AddTreeNodes(IHasTreeNodes parent, IReadOnlyList<DependencyObject> dependencies)
    {
        foreach (var dependency in dependencies)
        {
            var node = parent.AddNode(
                $"{Styling.Value(dependency.Reference)} {Styling.Muted($"({dependency.Type})")}");
            AddTreeNodes(node, dependency.Children);
        }
    }

    private static void RenderUnused(IReadOnlyList<DependencyObject> unused)
    {
        AnsiConsole.MarkupLine(Styling.Title($"Unused objects: {unused.Count}"));
        AnsiConsole.WriteLine();
        if (unused.Count == 0)
        {
            AnsiConsole.MarkupLine(Styling.Muted("None"));
            return;
        }

        var table = Styling.NewTable("Type", "Path");
        foreach (var dependency in unused)
            table.AddRow(
                Styling.MarkupEscape(dependency.Type),
                Styling.MarkupEscape(dependency.Path));
        AnsiConsole.Write(table);
    }

    private static object ToReferenceJson(DependencyObject dependency)
    {
        var json = new Dictionary<string, object?>
        {
            ["objectName"] = dependency.Reference,
            ["objectType"] = dependency.Type,
            ["path"] = dependency.Path
        };

        if (dependency.Children.Count > 0)
            json["children"] = dependency.Children.Select(ToReferenceJson).ToList();

        return json;
    }
}
