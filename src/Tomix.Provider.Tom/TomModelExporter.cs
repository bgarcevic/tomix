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
        return supportingFiles ? Directory.GetParent(target)!.FullName : target;
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
