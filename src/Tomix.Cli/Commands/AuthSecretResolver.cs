namespace Tomix.Cli.Commands;

/// <summary>
/// Resolves a secret without ever accepting its value on argv (docs/cli-ux-guidelines.md —
/// never accept secrets via flags or environment variables). Sources, in order: the '-'
/// sentinel (one line from stdin), a secret file, then an optional masked-prompt fallback.
/// Option validators reject any argv value other than '-' before this runs.
/// </summary>
internal static class AuthSecretResolver
{
    public sealed record SecretResolution(string? Secret, string? ErrorCode, string? ErrorMessage);

    public static SecretResolution Resolve(
        string? optionValue,
        string? filePath,
        string optionName,
        string fileOptionName,
        Func<string?> readStdinLine,
        Func<string?>? promptFallback = null)
    {
        if (optionValue == "-" && filePath is not null)
            return new(null, "TOMIX_AUTH_SECRET_SOURCE_CONFLICT",
                $"{optionName} - and {fileOptionName} cannot be combined; choose one.");

        if (optionValue == "-")
        {
            var line = readStdinLine()?.TrimEnd('\r');
            return string.IsNullOrEmpty(line)
                ? new(null, "TOMIX_AUTH_SECRET_REQUIRED", $"No secret was provided on stdin for {optionName} -.")
                : new(line, null, null);
        }

        if (filePath is not null)
        {
            if (!File.Exists(filePath))
                return new(null, "TOMIX_AUTH_SECRET_FILE_NOT_FOUND", $"Secret file not found: {filePath}");

            var contents = File.ReadAllText(filePath).TrimEnd('\r', '\n');
            return string.IsNullOrEmpty(contents)
                ? new(null, "TOMIX_AUTH_SECRET_REQUIRED", $"Secret file is empty: {filePath}")
                : new(contents, null, null);
        }

        if (promptFallback is not null)
        {
            var secret = promptFallback();
            return string.IsNullOrEmpty(secret)
                ? new(null, "TOMIX_AUTH_SECRET_REQUIRED", "No secret was entered at the prompt.")
                : new(secret, null, null);
        }

        return new(null, null, null);
    }
}
