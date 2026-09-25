using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Models;
using TabularJsonSerializer = Microsoft.AnalysisServices.Tabular.JsonSerializer;

namespace Tomix.Provider.Tom;

public static class TomModelExporter
{
    /// <summary>
    /// Model files are git artifacts, so the same model must serialize to the same bytes on
    /// every OS: pin LF newlines instead of the default <see cref="Environment.NewLine"/>
    /// (CRLF on Windows, LF on Unix), matching the TMDL convention.
    /// </summary>
    private static readonly JsonSerializerOptions IndentedLfJsonOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n"
    };

    public static Task<ModelExportResult> ExportAsync(
        Database database,
        ModelExportRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var format = NormalizeFormat(request.Serialization);
        var savedPath = format switch
        {
            "bim" => ExportBim(database, request.OutputPath, request.Overwrite),
            "tmdl" => ExportTmdl(database, request.OutputPath, request.Overwrite, request.SupportingFiles),
            _ => throw new NotSupportedException($"Unsupported serialization: {request.Serialization}")
        };

        return Task.FromResult(new ModelExportResult(savedPath, format));
    }

    private static string ExportTmdl(
        Database database,
        string outputPath,
        bool overwrite,
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

        EnsureWritable(target, overwrite);

        // Serialize into a staging folder, then sync only the files whose content changed into the
        // target: a small edit produces a small git diff, and a serializer failure leaves the
        // existing model untouched instead of half-deleted.
        var staging = Path.Combine(Path.GetTempPath(), $"tomix-tmdl-{Guid.NewGuid():N}");
        try
        {
            TmdlSerializer.SerializeDatabaseToFolder(database, staging);
            TmdlFolderSync.Apply(staging, target, AlignSourceBlocks);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* best-effort cleanup */ }
        }

        return supportingFiles ? Directory.GetParent(target)!.FullName : target;
    }

    /// <summary>
    /// Aligns the partition <c>source =</c> expression blocks of a freshly serialized TMDL file with
    /// the file it replaces. <see cref="TmdlSerializer"/> writes M source bodies two indentation
    /// levels below the property; Power BI Desktop writes them one level below (it agrees with the
    /// serializer on every other expression property, e.g. measures and calc items). Each M block is
    /// outdented to Desktop depth unless the same partition in <paramref name="existingText"/> sits
    /// at serializer depth, so a save never re-indents an unchanged block whichever convention the
    /// model was written with (#201). New partitions get Desktop depth.
    /// Lossless: TMDL strips the common leading whitespace of a delimited expression on parse.
    /// </summary>
    internal static string AlignSourceBlocks(string text, string? existingText)
    {
        var keepSerializerDepth = new HashSet<string>(StringComparer.Ordinal);
        if (existingText is not null)
        {
            foreach (var block in FindMSourceBlocks(existingText.Split('\n')))
            {
                if (block.MinContentIndent >= block.PropertyIndent + 2)
                    keepSerializerDepth.Add(block.Header);
            }
        }

        var lines = text.Split('\n');
        var changed = false;
        foreach (var block in FindMSourceBlocks(lines).ToList())
        {
            if (block.MinContentIndent < block.PropertyIndent + 2 || keepSerializerDepth.Contains(block.Header))
                continue;

            for (var j = block.PropertyLine + 1; j <= block.LastContentLine; j++)
            {
                if (CountLeadingTabs(lines[j]) >= block.PropertyIndent + 2)
                {
                    lines[j] = lines[j][1..];
                    changed = true;
                }
            }
        }

        return changed ? string.Join('\n', lines) : text;
    }

    /// <summary>
    /// Outdents the body of each bare <c>source =</c> delimited-expression block under an
    /// M partition (<c>partition X = m</c>) by one tab, but only when every content line sits at
    /// least two levels below the property (the serializer convention). Desktop indents M source
    /// bodies one level deep but DAX (<c>= calculated</c>) source bodies two levels deep, so
    /// calculated partitions, blocks already at Desktop depth, single-line sources, and fenced
    /// (<c>```</c>) expressions are left untouched, making the transform idempotent.
    /// </summary>
    internal static string OutdentSourceBlocks(string text) => AlignSourceBlocks(text, existingText: null);

    private readonly record struct SourceBlock(
        string Header, int PropertyLine, int PropertyIndent, int LastContentLine, int MinContentIndent);

    /// <summary>
    /// Finds each multiline bare <c>source =</c> block under an M partition. The block is the run of
    /// blank or deeper-indented lines that follows the property; <see cref="SourceBlock.Header"/> is
    /// the enclosing partition declaration, trimmed, which identifies the block across files.
    /// </summary>
    private static IEnumerable<SourceBlock> FindMSourceBlocks(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var propertyIndent = CountLeadingTabs(line);
            if (propertyIndent == 0 || line[propertyIndent..] != "source =")
                continue;

            var header = EnclosingMPartition(lines, i, propertyIndent);
            if (header is null)
                continue;

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

            if (minContentIndent != int.MaxValue)
                yield return new SourceBlock(header, i, propertyIndent, lastContent, minContentIndent);

            i = lastContent;
        }
    }

    /// <summary>
    /// Walks back from the <c>source =</c> property to the enclosing declaration (the nearest
    /// shallower-indented line) and returns it, trimmed, when it is an M partition.
    /// </summary>
    private static string? EnclosingMPartition(string[] lines, int sourceIndex, int propertyIndent)
    {
        for (var i = sourceIndex - 1; i >= 0; i--)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Trim().Length == 0)
                continue;

            var indent = CountLeadingTabs(line);
            if (indent >= propertyIndent)
                continue;

            var declaration = line.Trim();
            return declaration.StartsWith("partition ", StringComparison.Ordinal)
                   && declaration.EndsWith("= m", StringComparison.Ordinal)
                ? declaration
                : null;
        }

        return null;
    }

    private static int CountLeadingTabs(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == '\t')
            count++;
        return count;
    }

    private static string ExportBim(Database database, string outputPath, bool overwrite)
    {
        var target = Path.GetFullPath(outputPath);
        if (!Path.HasExtension(target))
            target += ".bim";

        var parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        if (File.Exists(target) && !overwrite)
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
        return node.ToJsonString(IndentedLfJsonOptions);
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

    private static void EnsureWritable(string path, bool overwrite)
    {
        if (!overwrite && Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new OutputExistsException($"Output directory already exists: {path}");
    }

    private static void WriteSupportingFiles(string semanticModel)
    {
        TmdlFolderSync.WriteIfChanged(Path.Combine(semanticModel, "definition.pbism"), """
            {
              "$schema": "https://developer.microsoft.com/json-schemas/fabric/item/semanticModel/definitionProperties/1.0.0/schema.json",
              "version": "4.2",
              "settings": {
                "qnaEnabled": true
              }
            }
            """);

        TmdlFolderSync.WriteIfChanged(Path.Combine(semanticModel, ".platform"), """
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
