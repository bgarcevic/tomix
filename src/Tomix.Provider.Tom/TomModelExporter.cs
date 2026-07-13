using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using TabularJsonSerializer = Microsoft.AnalysisServices.Tabular.JsonSerializer;

namespace Tomix.Provider.Tom;

public static class TomModelExporter
{
    public static Task<ModelExportResult> ExportAsync(
        Database database,
        ModelExportRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var format = NormalizeFormat(request.Serialization);
        var savedPath = format switch
        {
            "bim" => ExportBim(database, request.OutputPath, request.Force),
            "tmdl" => ExportTmdl(database, request.OutputPath, request.Force, request.SupportingFiles),
            _ => throw new NotSupportedException($"Unsupported serialization: {request.Serialization}")
        };

        return Task.FromResult(new ModelExportResult(savedPath, format));
    }

    private static string ExportTmdl(
        Database database,
        string outputPath,
        bool force,
        bool supportingFiles)
    {
        var target = Path.GetFullPath(outputPath);
        if (supportingFiles)
        {
            var semanticModel = Path.Combine(target, $"{SemanticModelName(database)}.SemanticModel");
            Directory.CreateDirectory(semanticModel);
            WriteSupportingFiles(semanticModel);
            target = Path.Combine(semanticModel, "definition");
        }

        PrepareDirectory(target, force);
        TmdlSerializer.SerializeDatabaseToFolder(database, target);
        AlignSourceBlocksWithDesktop(target);
        return supportingFiles ? Directory.GetParent(target)!.FullName : target;
    }

    /// <summary>
    /// Power BI Desktop writes partition <c>source =</c> expression blocks one indentation level
    /// above the property, while <see cref="TmdlSerializer"/> writes them two levels deep (it
    /// agrees with Desktop on every other expression property, e.g. measures and calc items).
    /// Outdenting those blocks by one tab makes a tomix save byte-identical to Desktop output,
    /// so saving a Desktop-authored model doesn't churn every table file in the user's git diff.
    /// Lossless: TMDL strips the common leading whitespace of a delimited expression on parse.
    /// </summary>
    private static void AlignSourceBlocksWithDesktop(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*.tmdl", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var normalized = OutdentSourceBlocks(text);
            if (!string.Equals(normalized, text, StringComparison.Ordinal))
                File.WriteAllText(file, normalized);
        }
    }

    /// <summary>
    /// Outdents the body of each bare <c>source =</c> delimited-expression block under an
    /// M partition (<c>partition X = m</c>) by one tab, but only when every content line sits at
    /// least two levels below the property (the serializer convention). Desktop indents M source
    /// bodies one level deep but DAX (<c>= calculated</c>) source bodies two levels deep, so
    /// calculated partitions, blocks already at Desktop depth, single-line sources, and fenced
    /// (<c>```</c>) expressions are left untouched, making the transform idempotent.
    /// </summary>
    internal static string OutdentSourceBlocks(string text)
    {
        var lines = text.Split('\n');
        var changed = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var propertyIndent = CountLeadingTabs(line);
            if (propertyIndent == 0 || line[propertyIndent..] != "source =")
                continue;

            if (!IsInsideMPartition(lines, i, propertyIndent))
                continue;

            // The block is the run of blank or deeper-indented lines that follows the property.
            var end = i + 1;
            var minContentIndent = int.MaxValue;
            var lastContent = i;
            while (end < lines.Length)
            {
                var candidate = lines[end].TrimEnd('\r');
                if (candidate.Trim().Length == 0)
                {
                    end++;
                    continue;
                }

                var indent = CountLeadingTabs(candidate);
                if (indent <= propertyIndent)
                    break;

                minContentIndent = Math.Min(minContentIndent, indent);
                lastContent = end;
                end++;
            }

            if (minContentIndent != int.MaxValue && minContentIndent >= propertyIndent + 2)
            {
                for (var j = i + 1; j <= lastContent; j++)
                {
                    if (CountLeadingTabs(lines[j]) >= propertyIndent + 2)
                    {
                        lines[j] = lines[j][1..];
                        changed = true;
                    }
                }
            }

            i = lastContent;
        }

        return changed ? string.Join('\n', lines) : text;
    }

    /// <summary>
    /// Walks back from the <c>source =</c> property to the enclosing declaration (the nearest
    /// shallower-indented line) and requires it to be an M partition.
    /// </summary>
    private static bool IsInsideMPartition(string[] lines, int sourceIndex, int propertyIndent)
    {
        for (var i = sourceIndex - 1; i >= 0; i--)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Trim().Length == 0)
                continue;

            var indent = CountLeadingTabs(line);
            if (indent >= propertyIndent)
                continue;

            return line[indent..].StartsWith("partition ", StringComparison.Ordinal)
                   && line.TrimEnd().EndsWith("= m", StringComparison.Ordinal);
        }

        return false;
    }

    private static int CountLeadingTabs(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == '\t')
            count++;
        return count;
    }

    private static string ExportBim(Database database, string outputPath, bool force)
    {
        var target = Path.GetFullPath(outputPath);
        if (!Path.HasExtension(target))
            target += ".bim";

        var parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        if (File.Exists(target) && !force)
            throw new OutputExistsException($"Output file already exists: {target}");

        File.WriteAllText(
            target,
            RemoveNullProperties(TabularJsonSerializer.SerializeDatabase(database, new SerializeOptions())));
        return target;
    }

    private static string RemoveNullProperties(string json)
    {
        var node = JsonNode.Parse(json);
        if (node is null)
            return json;

        RemoveNullProperties(node);
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void RemoveNullProperties(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (property.Value is null)
                    obj.Remove(property.Key);
                else
                    RemoveNullProperties(property.Value);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                if (child is not null)
                    RemoveNullProperties(child);
            }
        }
    }

    private static void PrepareDirectory(string path, bool force)
    {
        if (Directory.Exists(path))
        {
            if (Directory.EnumerateFileSystemEntries(path).Any())
            {
                if (!force)
                    throw new OutputExistsException($"Output directory already exists: {path}");

                ClearDirectory(path);
            }
        }
        else
        {
            Directory.CreateDirectory(path);
        }
    }

    private static void ClearDirectory(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var full = Path.Combine(path, entry);
            if (Directory.Exists(full))
                Directory.Delete(full, recursive: true);
            else
                File.Delete(full);
        }
    }

    private static void WriteSupportingFiles(string semanticModel)
    {
        File.WriteAllText(Path.Combine(semanticModel, "definition.pbism"), """
            {
              "$schema": "https://developer.microsoft.com/json-schemas/fabric/item/semanticModel/definitionProperties/1.0.0/schema.json",
              "version": "4.2",
              "settings": {
                "qnaEnabled": true
              }
            }
            """);

        File.WriteAllText(Path.Combine(semanticModel, ".platform"), """
            {
              "$schema": "https://developer.microsoft.com/json-schemas/fabric/gitIntegration/platformProperties/2.0.0/schema.json",
              "metadata": {
                "type": "SemanticModel"
              }
            }
            """);
    }

    private static string NormalizeFormat(string serialization)
    {
        var format = serialization.Trim().ToLowerInvariant();
        return format switch
        {
            "" or "auto" or "tmdl" => "tmdl",
            "bim" or "tmsl" => "bim",
            _ => format
        };
    }

    private static string SemanticModelName(Database database)
    {
        var name = string.IsNullOrWhiteSpace(database.Name) ? "SemanticModel" : database.Name;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        return name;
    }
}
