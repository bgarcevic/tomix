using System.Text.Json;
using Tomix.Core.Results;

namespace Tomix.App.Init;

public sealed class InitModelHandler
{
    public TomixResult<InitModelResult> Handle(InitModelRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OutputPath))
            return TomixResult<InitModelResult>.Fail(
                "TOMIX_INIT_OUTPUT_REQUIRED",
                "Output path is required. Provide [output-path] or --model.",
                exitCode: 2);

        var format = NormalizeSerialization(request.Serialization);
        if (format is not ("tmdl" or "bim" or "pbip"))
            return TomixResult<InitModelResult>.Fail(
                "TOMIX_INIT_UNSUPPORTED_SERIALIZATION",
                $"Unsupported serialization: {request.Serialization}",
                exitCode: 2);

        var compatibilityMode = NormalizeCompatibilityMode(request.CompatibilityMode);
        if (compatibilityMode is null)
            return TomixResult<InitModelResult>.Fail(
                "TOMIX_INIT_UNSUPPORTED_COMPATIBILITY_MODE",
                $"Unsupported compatibility mode: {request.CompatibilityMode}",
                exitCode: 2);

        var outputPath = Path.GetFullPath(request.OutputPath);
        var name = string.IsNullOrWhiteSpace(request.Name)
            ? DefaultName(outputPath, format)
            : request.Name.Trim();
        var compatibilityLevel = request.CompatibilityLevel ??
                                 (compatibilityMode == "PowerBI" ? 1702 : 1500);

        try
        {
            var created = format switch
            {
                "tmdl" => CreateTmdl(outputPath, name, compatibilityMode, compatibilityLevel, request.Force),
                "bim" => CreateBim(outputPath, name, compatibilityMode, compatibilityLevel, request.Force),
                "pbip" => CreatePbip(outputPath, name, compatibilityMode, compatibilityLevel, request.Force),
                _ => throw new InvalidOperationException()
            };

            return TomixResult<InitModelResult>.Ok(new InitModelResult(
                created,
                format,
                name,
                compatibilityLevel,
                compatibilityMode));
        }
        catch (IOException ex)
        {
            return TomixResult<InitModelResult>.Fail("TOMIX_INIT_OUTPUT_EXISTS", ex.Message, exitCode: 2);
        }
    }

    private static string CreateTmdl(
        string outputPath,
        string name,
        string compatibilityMode,
        int compatibilityLevel,
        bool force)
    {
        PrepareDirectory(outputPath, force);
        WriteTmdlDefinition(outputPath, name, compatibilityMode, compatibilityLevel);
        return outputPath;
    }

    private static string CreateBim(
        string outputPath,
        string name,
        string compatibilityMode,
        int compatibilityLevel,
        bool force)
    {
        var target = Path.HasExtension(outputPath)
            ? outputPath
            : Path.Combine(outputPath, "model.bim");
        var parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent))
            PrepareDirectory(parent, force: false);

        if (File.Exists(target) && !force)
            throw new IOException($"Output file already exists: {target}");

        File.WriteAllText(target, CreateBimJson(name, compatibilityMode, compatibilityLevel));
        return Path.HasExtension(outputPath) ? target : outputPath;
    }

    private static string CreatePbip(
        string outputPath,
        string name,
        string compatibilityMode,
        int compatibilityLevel,
        bool force)
    {
        PrepareDirectory(outputPath, force);

        var projectName = Path.GetFileName(outputPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var semanticModelPath = Path.Combine(outputPath, $"{projectName}.SemanticModel");
        var definitionPath = Path.Combine(semanticModelPath, "definition");
        Directory.CreateDirectory(definitionPath);
        WriteTmdlDefinition(definitionPath, name, compatibilityMode, compatibilityLevel);
        WriteSemanticModelFiles(semanticModelPath);

        var reportPath = Path.Combine(outputPath, $"{projectName}.Report");
        Directory.CreateDirectory(Path.Combine(reportPath, "definition", "pages"));
        File.WriteAllText(Path.Combine(reportPath, "definition", "report.json"), "{}");
        File.WriteAllText(Path.Combine(reportPath, "definition", "version.json"), "{\"version\":\"4.0\"}");
        File.WriteAllText(Path.Combine(reportPath, "definition", "pages", "pages.json"), "{\"pages\":[]}");
        File.WriteAllText(Path.Combine(reportPath, "definition.pbir"), "{}");
        File.WriteAllText(Path.Combine(reportPath, ".platform"), PlatformJson("Report"));

        File.WriteAllText(Path.Combine(outputPath, $"{projectName}.pbip"), PbipJson(projectName));
        return outputPath;
    }

    private static void WriteTmdlDefinition(
        string directory,
        string name,
        string compatibilityMode,
        int compatibilityLevel)
    {
        File.WriteAllText(Path.Combine(directory, "database.tmdl"), $"""
            database {TmdlIdentifier(name)}
            	id: SemanticModel
            	compatibilityLevel: {compatibilityLevel}
            	compatibilityMode: {compatibilityMode.ToLowerInvariant()}

            """);

        var modelBody = compatibilityMode == "PowerBI"
            ? """
              model Model
              	defaultPowerBIDataSourceVersion: powerBI_V3

              annotation __TEdtr = 1

              """
            : """
              model Model

              annotation __TEdtr = 1

              """;

        File.WriteAllText(Path.Combine(directory, "model.tmdl"), modelBody);
    }

    private static void WriteSemanticModelFiles(string semanticModelPath)
    {
        File.WriteAllText(Path.Combine(semanticModelPath, "definition.pbism"), """
            {
              "$schema": "https://developer.microsoft.com/json-schemas/fabric/item/semanticModel/definitionProperties/1.0.0/schema.json",
              "version": "4.2",
              "settings": {
                "qnaEnabled": true
              }
            }
            """);
        File.WriteAllText(Path.Combine(semanticModelPath, ".platform"), PlatformJson("SemanticModel"));
    }

    private static string CreateBimJson(string name, string compatibilityMode, int compatibilityLevel)
    {
        var model = new Dictionary<string, object?>
        {
            ["annotations"] = new[]
            {
                new Dictionary<string, string>
                {
                    ["name"] = "__TEdtr",
                    ["value"] = "1"
                }
            }
        };

        if (compatibilityMode == "PowerBI")
            model["defaultPowerBIDataSourceVersion"] = "powerBI_V3";

        var database = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["id"] = "SemanticModel",
            ["compatibilityLevel"] = compatibilityLevel,
            ["model"] = model
        };

        return JsonSerializer.Serialize(database, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string PbipJson(string projectName)
        => $$"""
            {
              "$schema": "https://developer.microsoft.com/json-schemas/fabric/pbip/pbipProperties/1.0.0/schema.json",
              "version": "1.0",
              "artifacts": [
                {
                  "report": {
                    "path": "{{projectName}}.Report"
                  }
                },
                {
                  "semanticModel": {
                    "path": "{{projectName}}.SemanticModel"
                  }
                }
              ],
              "settings": {
                "enableAutoRecovery": true
              }
            }
            """;

    private static string PlatformJson(string type)
        => $$"""
            {
              "$schema": "https://developer.microsoft.com/json-schemas/fabric/gitIntegration/platformProperties/2.0.0/schema.json",
              "metadata": {
                "type": "{{type}}"
              }
            }
            """;

    private static void PrepareDirectory(string path, bool force)
    {
        if (Directory.Exists(path))
        {
            if (force)
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.Delete(file);
                foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
                             .OrderByDescending(d => d.Length))
                    Directory.Delete(directory, recursive: true);
            }
        }
        else
        {
            Directory.CreateDirectory(path);
        }
    }

    private static string DefaultName(string outputPath, string format)
    {
        var trimmed = outputPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return format == "bim" && Path.HasExtension(trimmed)
            ? Path.GetFileName(trimmed)
            : Path.GetFileName(trimmed);
    }

    private static string NormalizeSerialization(string serialization)
        => string.IsNullOrWhiteSpace(serialization)
            ? "tmdl"
            : serialization.Trim().ToLowerInvariant();

    private static string? NormalizeCompatibilityMode(string compatibilityMode)
    {
        if (string.IsNullOrWhiteSpace(compatibilityMode))
            return "PowerBI";

        return compatibilityMode.Trim().ToLowerInvariant() switch
        {
            "powerbi" => "PowerBI",
            "analysisservices" => "AnalysisServices",
            _ => null
        };
    }

    private static string TmdlIdentifier(string name)
        => name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-')
            ? name
            : $"'{name.Replace("'", "''")}'";
}
