using Tomix.Core.Results;

namespace Tomix.App.Bpa;

public sealed record BpaRulesInitRequest(
    string? RulesFile = null,
    bool Overwrite = false);

/// <summary>
/// Scaffolds an empty rules file (<c>bpa rules init</c>) at the selected <c>--rules-file</c> or the
/// user's config-dir <c>bpa-rules.json</c>. An existing file is kept unless <c>--overwrite</c>.
/// </summary>
public sealed class BpaRulesInitHandler
{
    private readonly string _configDirectory;

    public BpaRulesInitHandler(string configDirectory) => _configDirectory = configDirectory;

    public TomixResult<BpaRulesFileResult> Handle(BpaRulesInitRequest request)
    {
        if (BpaRulesFile.TryResolvePath(_configDirectory, request.RulesFile, out var path) is { } remote)
            return remote;

        if (File.Exists(path) && !request.Overwrite)
            return TomixResult<BpaRulesFileResult>.Fail(
                "TOMIX_BPA_RULES_FILE_EXISTS",
                $"BPA rules file already exists: {path}",
                exitCode: 2,
                hint: "Pass --overwrite to replace it with an empty file.");

        var file = BpaRulesFile.Empty(path);
        file.Save();

        return TomixResult<BpaRulesFileResult>.Ok(new BpaRulesFileResult(
            "init", file.Path, Changed: true, RuleCount: 0));
    }
}
